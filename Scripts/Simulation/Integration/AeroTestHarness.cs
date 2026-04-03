#pragma warning disable
using System;
using Keen.VRage.Core;
using Keen.Game2.Simulation.WorldObjects.Movement;

namespace AeroMod;

// ═══════════════════════════════════════════════════════════════
// Test case and result types
// ═══════════════════════════════════════════════════════════════

public readonly struct TestCase
{
    public readonly string Name;
    public readonly Vector3 KickAxis;   // unit axis of perturbation (local frame)
    public readonly float TargetDeg;    // degrees to perturb
    public TestCase(string name, Vector3 kickAxis, float targetDeg)
    { Name = name; KickAxis = kickAxis; TargetDeg = targetDeg; }
}

public readonly struct TestResult
{
    public readonly string ScenarioName;
    public readonly string TestName;
    public readonly bool Passed;
    public readonly float FinalErrorDeg;
    public readonly float SettleTimeSec;
    public TestResult(string scenario, string test, bool passed, float errorDeg, float timeSec)
    { ScenarioName = scenario; TestName = test; Passed = passed; FinalErrorDeg = errorDeg; SettleTimeSec = timeSec; }
}

// ═══════════════════════════════════════════════════════════════
// Test scenario interface
// ═══════════════════════════════════════════════════════════════

public interface ITestScenario
{
    string Name { get; }
    List<TestCase> TestCases { get; }
    float PassErrorDeg { get; }
    float PassAngSpeed { get; }
    int HoldFrames { get; }
    int TimeoutFrames { get; }
    int StabilizeFrames { get; }
    void Setup(AeroGridComponent aero, WorldTransform wt);
    void Teardown(AeroGridComponent aero);
    void OnSettleFrame(AeroGridComponent aero, WorldTransform wt);
}

// ═══════════════════════════════════════════════════════════════
// Built-in: Space offset test (zero-g, gyros off, Bob-only)
// ═══════════════════════════════════════════════════════════════

internal class SpaceOffsetScenario : ITestScenario
{
    public string Name => "SpaceOffset";
    public float PassErrorDeg => 2f;
    public float PassAngSpeed => 0.05f;
    public int HoldFrames => 60;         // 1s hold
    public int TimeoutFrames => 900;     // 15s timeout
    public int StabilizeFrames => 180;   // 3s settle

    public List<TestCase> TestCases { get; } = new()
    {
        new("Pitch +10",  new Vector3(1, 0, 0),  10f),
        new("Pitch -10",  new Vector3(-1, 0, 0), 10f),
        new("Yaw +10",    new Vector3(0, 1, 0),  10f),
        new("Yaw -10",    new Vector3(0, -1, 0), 10f),
        new("Roll +10",   new Vector3(0, 0, 1),  10f),
        new("Roll -10",   new Vector3(0, 0, -1), 10f),
        new("Pitch +15",  new Vector3(1, 0, 0),  15f),
        new("Yaw +15",    new Vector3(0, 1, 0),  15f),
        new("Roll +15",   new Vector3(0, 0, 1),  15f),
    };

    public void Setup(AeroGridComponent aero, WorldTransform wt)
    {
        // Zero all velocity
        PhysicsHack.TrySetVelocity(aero.Data, Vector3.Zero, Vector3.Zero);

        // Clear thrust overrides
        for (int i = 0; i < aero._thrusterCache.Count; i++)
            OffsetThrustJob.ForceOverride(aero._thrusterCache[i].ThrusterComponent, 0f);

        Log.Default?.Info($"[TEST] {Name}: Setup complete. Gyros ON, phantom torque ON, {aero._thrusterCache.Count} thrusters, {aero._gyroCache.Count} gyros.");
    }

    public void OnSettleFrame(AeroGridComponent aero, WorldTransform wt)
    {
        // Kill linear drift (no gravity, but numerical noise)
        PhysicsHack.TrySetLinearVelocity(aero.Data, Vector3.Zero);
    }

    public void Teardown(AeroGridComponent aero)
    {
        Log.Default?.Info($"[TEST] {Name}: Teardown complete.");
    }
}

// ═══════════════════════════════════════════════════════════════
// Built-in: Thrust-only attitude (gyros off, phantom torque ON)
// ═══════════════════════════════════════════════════════════════

internal class ThrustOnlyAttitudeScenario : ITestScenario
{
    public string Name => "ThrustOnlyAttitude";
    public float PassErrorDeg => 2f;
    public float PassAngSpeed => 0.05f;
    public int HoldFrames => 60;
    public int TimeoutFrames => 900;
    public int StabilizeFrames => 180;

    public List<TestCase> TestCases { get; } = new()
    {
        new("Pitch +10",  new Vector3(1, 0, 0),  10f),
        new("Pitch -10",  new Vector3(-1, 0, 0), 10f),
        new("Yaw +10",    new Vector3(0, 1, 0),  10f),
        new("Yaw -10",    new Vector3(0, -1, 0), 10f),
        new("Roll +10",   new Vector3(0, 0, 1),  10f),
        new("Roll -10",   new Vector3(0, 0, -1), 10f),
        new("Pitch +15",  new Vector3(1, 0, 0),  15f),
        new("Yaw +15",    new Vector3(0, 1, 0),  15f),
        new("Roll +15",   new Vector3(0, 0, 1),  15f),
    };

    public void Setup(AeroGridComponent aero, WorldTransform wt)
    {
        if (aero._gyroCache.Count > 0)
            OffsetThrustJob.SetGyrosEnabled(aero._gyroCache, false);
        PhysicsHack.TryZeroGyroTorque(aero.Data);
        PhysicsHack.TrySetVelocity(aero.Data, Vector3.Zero, Vector3.Zero);

        Log.Default?.Info($"[TEST] {Name}: Setup complete. Gyros off, phantom torque ON, {aero._thrusterCache.Count} thrusters.");
    }

    public void OnSettleFrame(AeroGridComponent aero, WorldTransform wt)
    {
        PhysicsHack.TrySetLinearVelocity(aero.Data, Vector3.Zero);
    }

    public void Teardown(AeroGridComponent aero)
    {
        Log.Default?.Info($"[TEST] {Name}: Teardown complete.");
    }
}

// ═══════════════════════════════════════════════════════════════
// Test harness — static class, called from AeroSimJob
// ═══════════════════════════════════════════════════════════════

public static class AeroTestHarness
{
    public static bool Enabled = true;

    private enum Phase { Idle, SelectGrid, FreezeOthers, Stabilize, RunTests, Summary, Done }

    // ── Grid tracking ──
    private static readonly HashSet<AeroGridComponent> _allGrids = new();
    private static readonly HashSet<AeroGridComponent> _frozenGrids = new();
    private static AeroGridComponent _testGrid;

    // ── State machine ──
    private static Phase _phase = Phase.Idle;
    private static int _frame;
    private static int _testIndex;
    private static int _subPhase;     // 0=perturb, 1=settle
    private static int _holdFrames;
    private static int _scenarioIndex;
    private static Quaternion _baseOrientation;

    // ── Scenarios ──
    private static readonly List<ITestScenario> _scenarios = new();
    private static ITestScenario _activeScenario;
    private static readonly List<TestResult> _results = new();

    // ── Per-grid world transform cache (updated each Tick) ──
    private static readonly Dictionary<AeroGridComponent, WorldTransform> _gridTransforms = new();

    private const float Rad2Deg = 180f / MathF.PI;

    static AeroTestHarness()
    {
        _scenarios.Add(new SpaceOffsetScenario());
        // ThrustOnlyAttitude disabled — functionally identical to SpaceOffset with phantom torque ON.
        // Re-enable when testing thruster-only (SuppressPhantomTorque) control.
        // _scenarios.Add(new ThrustOnlyAttitudeScenario());
    }

    // ── Grid lifecycle ──

    public static void TrackGrid(AeroGridComponent grid)
    {
        _allGrids.Add(grid);
    }

    public static void UntrackGrid(AeroGridComponent grid)
    {
        _allGrids.Remove(grid);
        _frozenGrids.Remove(grid);
        _gridTransforms.Remove(grid);
        if (_testGrid == grid)
        {
            _testGrid = null;
            _phase = Phase.Done;
        }
    }

    /// <summary>Returns true if this grid should be skipped (frozen during test).</summary>
    public static bool ShouldSkipGrid(AeroGridComponent grid)
    {
        return Enabled && _frozenGrids.Contains(grid);
    }

    // ══════════════════════════════════════════════════════════════
    //  Main entry point — called from AeroSimJob every frame
    // ══════════════════════════════════════════════════════════════

    public static void Tick(AeroGridComponent aero, WorldTransform wt)
    {
        if (!Enabled || _phase == Phase.Done) return;

        // Cache world transform for grid selection
        _gridTransforms[aero] = wt;

        // Only the test grid runs the state machine
        if (_testGrid != null && _testGrid != aero) return;

        switch (_phase)
        {
            case Phase.Idle:
                _frame++;
                // Wait 3s for world to load
                if (_frame >= 180 && _allGrids.Count > 0)
                    _phase = Phase.SelectGrid;
                break;

            case Phase.SelectGrid:
                SelectGrid();
                break;

            case Phase.FreezeOthers:
                FreezeOtherGrids();
                _scenarioIndex = 0;
                _results.Clear();
                StartScenario();
                break;

            case Phase.Stabilize:
                RunStabilize(aero, wt);
                break;

            case Phase.RunTests:
                RunTests(aero, wt);
                break;

            case Phase.Summary:
                PrintSummary();
                _activeScenario.Teardown(aero);
                _scenarioIndex++;
                if (_scenarioIndex < _scenarios.Count)
                    StartScenario();
                else
                    Finish();
                break;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  Phase implementations
    // ══════════════════════════════════════════════════════════════

    private static int _selectAttempts;

    private static void SelectGrid()
    {
        _selectAttempts++;

        AeroGridComponent best = null;
        double bestDistSq = double.MaxValue;
        Vector3D focusPos = AeroGridComponent.DebugFocusPosition;

        foreach (var grid in _allGrids)
        {
            if (grid._thrusterCache.Count < 4) continue;

            if (_gridTransforms.TryGetValue(grid, out var gwt))
            {
                double distSq = (gwt.Position - focusPos).LengthSquared();
                // Prefer piloted grids
                if (grid.Data.Has<TargetControlData>())
                    distSq *= 0.01; // strong preference
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = grid;
                }
            }
        }

        if (best == null)
        {
            // Log once per second with diagnostic detail
            if (_selectAttempts % 60 == 1)
            {
                int maxThrusters = 0;
                foreach (var g in _allGrids)
                    if (g._thrusterCache.Count > maxThrusters) maxThrusters = g._thrusterCache.Count;
                Log.Default?.Info($"[TEST] No eligible grid found (need >= 4 thrusters). " +
                    $"{_allGrids.Count} grids tracked, max thrusters on any grid: {maxThrusters}");
            }
            return;
        }

        _testGrid = best;
        PhysicsHack.TryGetMassProperties(best.Data, out float mass, out _);
        Log.Default?.Info($"[TEST] Selected grid: {best._thrusterCache.Count} thrusters, mass={mass:F0}kg, " +
            $"{_allGrids.Count} total grids");
        _phase = Phase.FreezeOthers;
    }

    private static void FreezeOtherGrids()
    {
        int frozenCount = 0;
        foreach (var grid in _allGrids)
        {
            if (grid == _testGrid) continue;
            _frozenGrids.Add(grid);
            PhysicsHack.TrySetVelocity(grid.Data, Vector3.Zero, Vector3.Zero);
            frozenCount++;
        }
        if (frozenCount > 0)
            Log.Default?.Info($"[TEST] Froze {frozenCount} other grids");
    }

    private static void StartScenario()
    {
        _activeScenario = _scenarios[_scenarioIndex];
        _frame = 0;
        _testIndex = 0;
        _subPhase = 0;
        _holdFrames = 0;
        _phase = Phase.Stabilize;
        Log.Default?.Info($"[TEST] ══════════════════════════════════════════════");
        Log.Default?.Info($"[TEST] Starting scenario: {_activeScenario.Name}");
        Log.Default?.Info($"[TEST] {_activeScenario.TestCases.Count} test cases, " +
            $"pass < {_activeScenario.PassErrorDeg}deg, timeout {_activeScenario.TimeoutFrames / 60f:F0}s");
        Log.Default?.Info($"[TEST] ══════════════════════════════════════════════");
    }

    private static void RunStabilize(AeroGridComponent aero, WorldTransform wt)
    {
        _frame++;

        if (_frame == 1)
        {
            _activeScenario.Setup(aero, wt);
        }

        // Hold still during stabilize
        PhysicsHack.TrySetVelocity(aero.Data, Vector3.Zero, Vector3.Zero);

        if (_frame >= _activeScenario.StabilizeFrames)
        {
            // Capture baseline orientation
            _baseOrientation = wt.Orientation;
            aero._holdOrientation = _baseOrientation;
            aero._holdOrientationValid = true;

            Log.Default?.Info($"[TEST] Stabilized. Running tests...");
            _phase = Phase.RunTests;
            _frame = 0;
            _testIndex = 0;
            _subPhase = 0;
        }
    }

    private static void RunTests(AeroGridComponent aero, WorldTransform wt)
    {
        if (_testIndex >= _activeScenario.TestCases.Count)
        {
            _phase = Phase.Summary;
            return;
        }

        var tc = _activeScenario.TestCases[_testIndex];

        // ── Sub-phase 0: PERTURB ──
        if (_subPhase == 0)
        {
            Log.Default?.Info($"[TEST] {_activeScenario.Name} | {_testIndex + 1}/{_activeScenario.TestCases.Count}: {tc.Name} ({tc.TargetDeg:F0}deg)");

            float rad = tc.TargetDeg * MathF.PI / 180f;
            Quaternion perturbation = Quaternion.CreateFromAxisAngle(tc.KickAxis, rad);
            PhysicsHack.TrySetOrientation(aero.Data, _baseOrientation * perturbation);
            PhysicsHack.TrySetVelocity(aero.Data, Vector3.Zero, Vector3.Zero);

            // Harness controls attitude during settle
            aero._holdOrientation = _baseOrientation;
            aero._holdOrientationValid = true;
            aero.HarnessControlsAttitude = true;

            // Clear thrust overrides
            for (int i = 0; i < aero._thrusterCache.Count; i++)
                OffsetThrustJob.ForceOverride(aero._thrusterCache[i].ThrusterComponent, 0f);

            _subPhase = 1;
            _frame = 0;
            _holdFrames = 0;
            return;
        }

        // ── Sub-phase 1: SETTLE ──
        _frame++;
        _activeScenario.OnSettleFrame(aero, wt);

        // Force attitude hold target every frame (overrides UpdateAttitudeHold re-capture)
        aero._holdOrientation = _baseOrientation;
        aero._holdOrientationValid = true;

        // Axis-angle error (no euler coupling)
        PhysicsHack.TryGetVelocity(aero.Data, out _, out var angWorld);
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angWorld, wt);

        Quaternion errQ = Quaternion.Inverse(wt.Orientation) * _baseOrientation;
        if (errQ.W < 0) errQ = new Quaternion(-errQ.X, -errQ.Y, -errQ.Z, -errQ.W);
        Vector3 axisAngleError = new Vector3(errQ.X, errQ.Y, errQ.Z) * 2f;
        float errorDeg = axisAngleError.Length() * (180f / MathF.PI);

        // PD attitude command — drives _lastGridAngVel for both thrusters and phantom torque
        // Kp=3: strong drive toward target. Kd=2: moderate damping to prevent overshoot.
        const float Kp = 3.0f;
        const float Kd = 2.0f;
        const float HoldMax = 2.0f;
        Vector3 errorClamped = new Vector3(
            Math.Clamp(axisAngleError.X, -0.5f, 0.5f),
            Math.Clamp(axisAngleError.Y, -0.5f, 0.5f),
            Math.Clamp(axisAngleError.Z, -0.5f, 0.5f));
        Vector3 attitudeCmd = errorClamped * Kp - localAngVel * Kd;
        aero._lastGridAngVel = new Vector3(
            Math.Clamp(attitudeCmd.X, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Y, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Z, -HoldMax, HoldMax));

        // Zero SasTorque — phantom PD in AeroSimJob uses _lastGridAngVel directly
        aero.SasTorque = Vector3.Zero;

        float angSpeed = localAngVel.Length();

        // 1Hz logging
        if (_frame % 60 == 0)
        {
            Log.Default?.Info($"[TEST] {_activeScenario.Name} | {tc.Name} | t={_frame / 60f:F1}s err={errorDeg:F2}deg w={angSpeed:F4}" +
                $" cmd=({aero._lastGridAngVel.X:F3},{aero._lastGridAngVel.Y:F3},{aero._lastGridAngVel.Z:F3})");
        }

        // Check pass
        if (errorDeg < _activeScenario.PassErrorDeg && angSpeed < _activeScenario.PassAngSpeed)
        {
            _holdFrames++;
            if (_holdFrames >= _activeScenario.HoldFrames)
            {
                float sec = _frame / 60f;
                Log.Default?.Info($"[TEST] >> PASS: {tc.Name} in {sec:F2}s (err={errorDeg:F2}deg)");
                _results.Add(new TestResult(_activeScenario.Name, tc.Name, true, errorDeg, sec));
                AdvanceTest();
                return;
            }
        }
        else
        {
            _holdFrames = 0;
        }

        // Timeout
        if (_frame >= _activeScenario.TimeoutFrames)
        {
            float sec = _frame / 60f;
            Log.Default?.Info($"[TEST] >> FAIL: {tc.Name} timeout (err={errorDeg:F2}deg w={angSpeed:F4})");
            _results.Add(new TestResult(_activeScenario.Name, tc.Name, false, errorDeg, sec));
            AdvanceTest();
        }
    }

    private static void AdvanceTest()
    {
        _testIndex++;
        _subPhase = 0;
        _frame = 0;
        _holdFrames = 0;
    }

    private static void PrintSummary()
    {
        string name = _activeScenario.Name;
        int pass = 0, fail = 0;
        foreach (var r in _results)
        {
            if (r.ScenarioName == name)
            {
                if (r.Passed) pass++; else fail++;
            }
        }

        Log.Default?.Info($"[TEST] ══════════════════════════════════════════════");
        Log.Default?.Info($"[TEST] RESULTS: {name}  ({pass}/{pass + fail} PASSED)");
        Log.Default?.Info($"[TEST] ──────────────────────────────────────────────");
        foreach (var r in _results)
        {
            if (r.ScenarioName == name)
            {
                string status = r.Passed ? "PASS" : "FAIL";
                string extra = r.Passed ? "" : " (TIMEOUT)";
                Log.Default?.Info($"[TEST]  {status}  {r.TestName,-16} err={r.FinalErrorDeg:F2}deg  t={r.SettleTimeSec:F2}s{extra}");
            }
        }
        Log.Default?.Info($"[TEST] ══════════════════════════════════════════════");
    }

    private static void Finish()
    {
        // Print grand total
        int totalPass = 0, totalFail = 0;
        foreach (var r in _results)
        {
            if (r.Passed) totalPass++; else totalFail++;
        }
        Log.Default?.Info($"[TEST] ALL SCENARIOS COMPLETE: {totalPass}/{totalPass + totalFail} PASSED");

        // Release harness control
        if (_testGrid != null)
            _testGrid.HarnessControlsAttitude = false;

        // Unfreeze all grids
        _frozenGrids.Clear();
        _testGrid = null;
        _phase = Phase.Done;
        Log.Default?.Info("[TEST] All grids unfrozen. Harness done.");
    }

    /// <summary>Register a custom test scenario (call before first Tick).</summary>
    public static void RegisterScenario(ITestScenario scenario)
    {
        _scenarios.Add(scenario);
    }
}
