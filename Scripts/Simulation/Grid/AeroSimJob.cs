#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Simulation;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// Simulation-rate (60Hz) job for aerodynamic force computation and application.
/// Runs during GridUpdateOrder.UpdateSystems.Update, after vanilla thrust has been applied.
/// Debug visualization stays in AeroDebugDraw (render-rate).
/// </summary>
public partial class AeroGridComponent
{
    private static int _simDragLogCooldown;
    private int _simFrameCount;

    [During(typeof(GridUpdateOrder.UpdateSystems.Update))]
    [After(typeof(ThrustComponent.OnComputeThrust))]
    private class OnAeroSim : JobGroup;

    /// <summary>After every grid system (the game's thrust and gyros, this mod's air), before the physics step: the attitude
    /// control (ThrustTorque) - it takes back the game gyros' turn when the aero-aware law flies them, so it must come
    /// after them.</summary>
    [After(typeof(GridUpdateOrder.UpdateSystems))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    private class OnAttitude : JobGroup;

    [OnAttitude]
    [MustHave(typeof(AeroGridComponent))]
    private static void AttitudeJob(AeroGridComponent aero, WorldTransform wt)
    {
        if (!aero._initialized || !AeroSwitch.Enabled || AeroTestHarness.ShouldSkipGrid(aero)) return;
        if (!ThrustTorque.Enabled || (aero._thrusterCache.Count == 0 && aero._gyroCache.Count == 0)) return;
        // (a quiet grid - the sim job's quiet path, this tick - was at rest for QuietAfter steps: ThrustTorque's rest branch
        //  ran each of them and darkened its flames; nothing to do until it stirs)
        if (QuietGate && aero._quietFrames > QuietAfter && aero._quietFrames % QuietRefresh != 0) return;
        long tth2 = AeroCost.Start();
        AeroCost.EnterSim();
        try
        {
            if (aero._thrusterCache.Count > 0 && aero._clearedOverride != 1)
            {
                // (the old system wrote a grid-level thrust override: cleared once, so the game's own thrust,
                // dampeners and gravity compensation run untouched)
                PhysicsHack.TrySetOverriddenThrust(aero.Entity, aero.Data, Vector3.Zero);
                aero._clearedOverride = 1;
            }
            PhysicsHack.TryGetVelocity(aero.Data, out _, out Vector3 angVel);
            aero.LastThrust = ThrustTorque.Apply(aero, wt, angVel, AeroPhysics.Dt);
            aero.LastGyroAware = aero.LastThrust.Aware; aero.LastGyroTorque = aero.LastThrust.Aware ? aero.LastThrust.GyroTorque : Vector3.Zero;
            // (the predicted loss-of-control warning's inputs: Loac.cs)
            aero.LoacHasTarget = aero.LastThrust.Aware && aero.LastThrust.HasTarget; aero.LoacTarget = aero.LastThrust.Target; aero.LoacReach = aero.LastThrust.Reach;
            if (PitchLog && aero.IsServerScene && aero.Data.Has<TargetControlData>() && aero._simFrameCount % 6 == 0)
            {
                PhysicsHack.TryGetVelocity(aero.Data, out _, out var w2);
                var wl = WorldTransform.TransformDirectionInv(w2, wt); var r = aero.LastThrust;
                Log.Default?.Info($"[AERO-PITCH] v={aero.LastSpeed:F0} M={(aero.HasResult ? aero.LastResult.Mach : 0):F2} w=({wl.X:F3},{wl.Y:F3},{wl.Z:F3}) err=({r.Error.X * 57.3f:F2},{r.Error.Y * 57.3f:F2},{r.Error.Z * 57.3f:F2})deg"
                    + $" aeroT=({aero.LastAeroTorque.X / 1000:F0},{aero.LastAeroTorque.Y / 1000:F0},{aero.LastAeroTorque.Z / 1000:F0})kNm gyro=({r.GyroTorque.X / 1000:F0},{r.GyroTorque.Y / 1000:F0},{r.GyroTorque.Z / 1000:F0})kNm"
                    + $" rcs=({r.RcsTorque.X / 1000:F0},{r.RcsTorque.Y / 1000:F0},{r.RcsTorque.Z / 1000:F0})kNm undone=({r.GameGyroUndone.X:F4},{r.GameGyroUndone.Y:F4},{r.GameGyroUndone.Z:F4}) aware={(r.Aware ? 1 : 0)} mode={r.Mode}"
                    + $" surf=({aero.LastSurfaceTorque.X / 1000:F0},{aero.LastSurfaceTorque.Y / 1000:F0},{aero.LastSurfaceTorque.Z / 1000:F0})kNm dem=({aero.LastControlTorque.X / 1000:F0},{aero.LastControlTorque.Y / 1000:F0},{aero.LastControlTorque.Z / 1000:F0})kNm alloc={aero._allocN}/{aero._allocSat}sat");
            }
        }
        finally { AeroCost.ExitSim(); }
        AeroCost.Thrust.Stop(tth2);
    }

    /// <summary>After the game's wind push (RangedAffectGenerators: WindUpdate.ApplyForceGroup), before the physics step:
    /// on a grid aero applies its forces to, the game's own wind push taken back (AeroWind - aero's air has the wind in).</summary>
    [After(typeof(Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.RangedAffectGenerators))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    private class OnWindUndo : JobGroup;

    [OnWindUndo]
    [MustHave(typeof(AeroGridComponent))]
    private static void WindUndoJob(AeroGridComponent aero, WorldTransform wt)
    {
        if (!aero._initialized || !AeroSwitch.Enabled || !AeroWind.Enabled || !aero.AeroForcesApplied) return;
        aero.LastWindUndone = AeroWind.UndoGameForce(aero.Data, wt);
    }

    [OnAeroSim]
    [MustHave(typeof(AeroGridComponent))]
    private static void AeroSimJob(AeroGridComponent aero, WorldTransform wt)
    {
        // (TEARING: something queued or tied - arm this tick's tear job, StructureTear.cs; idle: this one read. Written only when
        //  not armed yet: every server grid's parallel job wrote the shared word each tick, its cache line bouncing between
        //  the cores - now longer, through a crack's run, 2026-10-15 review)
        if (StructureTear.Work != 0 && aero.IsServerScene && System.Threading.Volatile.Read(ref StructureTear.Armed) == 0) StructureTear.Armed = 1;
        long t0 = AeroCost.Start();
        AeroCost.EnterSim();
        try { AeroSimJobCore(aero, wt); } finally { AeroCost.ExitSim(); }
        AeroCost.Sim.Stop(t0);
        AeroCost.Watch(aero);
        if (AeroCost.Log) { if (aero.IsServerScene) Interlocked.Increment(ref AeroCost.SimServer); else Interlocked.Increment(ref AeroCost.SimClient); }
        AeroCost.WatchThrust(aero);
        AeroCost.MaybeLog();
    }

    /// <summary>The quiet path (AeroSimJobCore): on, the steps a grid must stay quiet first, and its full step's period.</summary>
    public static bool QuietGate = true;
    public static int QuietAfter = 30, QuietRefresh = 30;
    internal int _quietFrames;
    [ThreadStatic] static int _tQuietSkips;

    /// <summary>Nothing for the aero model to do: nobody flying it (seat focus, a target), no engine pushing, no air past it,
    /// not turning, no entry glow, no external entry source, no test airflow.</summary>
    bool IsQuiet(Vector3 airVel, Vector3 angVel)
    {
        if (airVel.LengthSquared() > 0.0025f || angVel.LengthSquared() > 4e-6f) return false;
        if (EntryHeat > 1e-3f || FlowPublished || AeroEntryFx.TestSpeed > 0f || AeroEntryFx.TestForcesOn) return false;
        // (the test harnesses tick at the end of the full step and count its frames: never quiet under them)
        if (AeroTestHarness.Enabled || AeroFlightTest.Enabled || AeroSpeedSpike.Enabled) return false;
        if (System.Diagnostics.Stopwatch.GetTimestamp() < StructFocusUntil || Data.Has<TargetControlData>()) return false;
        if (Data.Has<ActiveThrustData>()) return false;
        // (an external entry source - a grid on another mod's rails - checked on the full steps only: it ends quiet there)
        if (_quietFrames % QuietRefresh == 0 && AeroEntryFx.ExternalAt(Entity, out _, out float es) && es > 0f) return false;
        return true;
    }

    /// <summary>DEV: every 6th tick, a piloted grid's rates and attitude error (local), the air's torque, and what the
    /// attitude control gave (gyros, thrusters) and took back of the game gyros' own turn.</summary>
    public static bool PitchLog;

    private static void AeroSimJobCore(AeroGridComponent aero, WorldTransform wt)
    {
        if (!aero._initialized) return;
        if (!AeroSwitch.Enabled) { AeroSwitch.WasOff = true; return; }
        if (AeroSwitch.WasOff) { AeroSwitch.WasOff = false; System.Threading.Interlocked.Increment(ref AeroSwitch.Generation); }
        if (aero._switchGen != AeroSwitch.Generation) { aero._switchGen = AeroSwitch.Generation; aero.ForceFullRebuild(); }

        // Test harness: skip frozen grids
        if (AeroTestHarness.ShouldSkipGrid(aero))
        {
            PhysicsHack.TrySetVelocity(aero.Data, Vector3.Zero, Vector3.Zero);
            return;
        }

        aero._simFrameCount++;
        AeroStats.BeginGrid();
        long tpre = AeroCost.Start();

        // (no spin zeroed on a grid's first frame - 2026-10-15: it was there to clear saved-world spin, and took the tumble
        //  of every piece broken off, every re-added grid and every client copy on its first frame: a grid's spin is physics)

        // ── Read physics state ──
        PhysicsHack.TryGetVelocity(aero.Data, out Vector3 linVel, out Vector3 angVel);
        // (torn-edge smoke waiting for its client grid: this server grid's pose, read here on its own thread - TearSmoke.Pairs)
        if (aero.IsServerScene && TearSmoke.Pending != 0) TearSmoke.ServerPose(aero.Entity, wt, linVel, angVel);
        if (aero.IsServerScene) DesignProbe.PublishPose(aero, wt);   // (the same for the design probe's pairing, while it is asked)
        // the air: the grid's velocity less the wind's (AeroWind) - what the aero model, Mach and the effects meet
        aero.LastWind = AeroWind.At(aero.Data);
        Vector3 airVel = linVel - aero.LastWind;

        // ── QUIET (2026-10-14, the user: "reduce performance cost"): a grid nobody flies, not thrusting, with no air past it
        // (the wind included), not turning, its entry glow gone - parked ships, stations, debris, most of a world's ~150
        // grids - had the whole step every tick (the ground ray, the mass, the inertia, the model: ~3 us x 2 scenes x 60 Hz
        // each, 55 ms/s in all). Quiet for QuietAfter steps in a row, it does only the reads above, and the full step once
        // every QuietRefresh steps (its caches, the probes, the clocks kept fresh); anything changing ends it at once.
        if (QuietGate && aero.IsQuiet(airVel, angVel))
        {
            if (++aero._quietFrames > QuietAfter && aero._quietFrames % QuietRefresh != 0)
            {
                aero.LastLinVel = linVel; aero.LastAngVel = angVel; aero.LastSpeed = linVel.Length();
                aero.AeroForcesApplied = false; aero.LastAeroTorque = Vector3.Zero; aero.LastSurfaceTorque = Vector3.Zero;
                AeroCost.Count(ref _tQuietSkips, ref AeroStats.QuietSkips);   // (per thread, a batch at a time: AeroCost.Count)
                AeroCost.Pre.Stop(tpre);
                return;
            }
        }
        else aero._quietFrames = 0;
        // (the mass and the inertia: one read of the same data - the inertia used below as before)
        bool massRead = PhysicsHack.TryGetMassAndInertia(aero.Data, out float mass, out Vector3 com, out var invI, out var majorAxisRot);

        float density = 0f;
        if (aero.Data.TryGet<AirData>(out var air))
            density = air.Density;
        // (above the game's air - it stops dead at the generator's affect distance - aero's thin upper tail: AtmosphereTail)
        density = AtmosphereProbe.WithTail(aero.Data.GetWorldTransform().Position, density);
        if (aero.IsServerScene)
        {
            if (!AeroSettings.SawServer) AeroSettings.SawServer = true;
            AtmosphereProbe.Tick(aero);   // (the API's point query: the generator list, every ~10 s)
            AeroSettings.Poll();          // (the settings files, every ~2 s)
        }

        // ── Ground height (sync raycast, every 3-10 ticks) ──
        if (aero.IsServerScene)
            PhysicsHack.InitGroundSystem(aero.Entity);   // (cheap when unchanged: the server session's physics)

        Vector3 gravity = PhysicsHack.GetGravityDirection(aero.Data);
        aero.InGravity = gravity.LengthSquared() > 1e-4f;
        aero.LastGravity = gravity.Length();
        aero.GroundHeight = aero.IsServerScene ? PhysicsHack.GetGroundDistance(aero.Ground, wt.Position, gravity, aero.Entity) : -1f;   // (rays in the server's world only)
        aero.GroundUpLocal = aero.InGravity ? -Vector3.Normalize(WorldTransform.TransformDirectionInv(gravity, wt)) : Vector3.Zero;

        // ── Cache physics state for draw job ──
        aero.LastLinVel = linVel;
        aero.LastAngVel = angVel;
        aero.LastCoM = com;
        aero.LastMass = mass;
        aero.LastDensity = density;
        aero.LastSpeed = linVel.Length();
        if (massRead)
        {
            aero.LastInvInertia = invI;
            aero.LastInertiaMajorAxisRot = majorAxisRot;
        }

        AeroCost.Pre.Stop(tpre);
        // ── Thruster cache rebuild (before TryCompute so Phase 8 sees thrusters) ──
        if (aero._thrusterCacheDirty)
        {
            if (aero._blockSize <= 0)
                aero._blockSize = aero.DetectBlockSize();
            if (aero._blockSize > 0)
            {
                long tc = AeroCost.Start();
                OffsetThrustJob.RebuildThrusterCache(aero._octree, aero._blockSize, aero._thrusterCache, aero.Entity);
                AeroCost.Thrusters.Stop(tc);
                aero._thrusterCacheDirty = false;
                aero._rcsGeomCount = -1;   // (ThrustTorque recomputes the thrusters' geometry)
                aero.ThrustersRebuilt();
            }
        }

        // ── Gyro cache rebuild ──
        if (aero._gyroCacheDirty)
        {
            long tg = AeroCost.Start();
            OffsetThrustJob.RebuildGyroCache(aero.Entity, aero._gyroCache);
            AeroCost.Gyros.Stop(tg);
            aero._gyroCacheDirty = false;
        }

        // ── Clear stale override values on first frame (persisted from save) ──
        // Only for the legacy offset-thrust path (ThrustTorque off), which writes overrides itself: with the default
        // ThrustTorque nothing of ours is in them, and this wiped the PLAYER's terminal overrides on every load.
        if (aero._simFrameCount == 1 && !ThrustTorque.Enabled)
        {
            // Clear per-thruster ThrusterOverrideData
            for (int i = 0; i < aero._thrusterCache.Count; i++)
                OffsetThrustJob.ForceOverride(aero._thrusterCache[i], 0f);
            // Clear grid-level OverriddenThrustData
            PhysicsHack.TrySetOverriddenThrust(aero.Entity, aero.Data, Vector3.Zero);
        }

        // ── Aero computation ──
        long tco = System.Diagnostics.Stopwatch.GetTimestamp();   // (the frame budget's clock too: always read)
        aero.CachedPriority = aero.BuildPriority;
        AeroFrameBudget.Touch();
        int every = aero.ComputeInterval;
        bool due = every <= 1 || !aero.HasResult || ((aero._simFrameCount + aero.LodPhase) % every) == 0 || aero.BudgetSkips > 0;
        // the frame budget: once spent, due grids wait (not piloted / fast ones, nor one that has waited MaxSkips)
        if (due && every > 1 && aero.HasResult && AeroFrameBudget.Spent && aero.BudgetSkips < AeroFrameBudget.MaxSkips)
        {
            aero.BudgetSkips++;
            due = false;
            if (AeroCost.Log) System.Threading.Interlocked.Increment(ref AeroFrameBudget.SkippedThisSecond);   // (read by the [AERO-COST] log only)
        }
        if (due)
        {
            aero.BudgetSkips = 0;
            if (AeroEntryFx.TestForcesOn)   // (TEST: the forces at the test airflow, measured not applied)
                aero.TryCompute(wt, MathF.Max(density, 1.2f), AeroEntryFx.TestVelocityWorld(wt), angVel, com, aero.GroundHeight);
            else
                aero.TryCompute(wt, density, airVel, angVel, com, aero.GroundHeight);
            if (AeroCost.Log) System.Threading.Interlocked.Increment(ref AeroFrameBudget.ComputedThisSecond);   // (read by the [AERO-COST] log only)
        }
        else
            aero.SkipCompute(wt, angVel);   // (the last forces, in the grid's frame, are applied again below)
        AeroFrameBudget.Add(System.Diagnostics.Stopwatch.GetTimestamp() - tco);
        if (AeroCost.Log) AeroCost.Compute.Stop(tco);
        AeroEntryFx.Flow(aero, wt, airVel, density, linVel);
        AeroEntryFx.Server(aero, wt, airVel, density, AeroPhysics.Dt);
        if (aero.IsServerScene) aero.TickWingStrain(AeroPhysics.Dt);   // (under TestForces too: the forces at the test airflow strain the wings - damage only with Structure.WingStrain on)   // (wing strain: WingStrainServer.cs)

        if (aero._simFrameCount <= 5 && aero.LastSpeed > 1f)
        {
            Log.Default?.Info($"[AERO-SIM] EARLY f={aero._simFrameCount} v={aero.LastSpeed:F1} " +
                $"d={density:F4} mass={mass:F0} hasResult={aero.HasResult} " +
                $"angVel=({angVel.X:F3},{angVel.Y:F3},{angVel.Z:F3})");
        }

        // ── Apply aero forces + torques ──
        // Fixed sim step: UpdateTime.UPDATE_STEPS_PER_SECOND is a compile-time constant in SE2.
        float dt = AeroPhysics.Dt;

        // Gyro handoff: when a pilot is present (TargetControlData) and error is large,
        // let game gyros handle coarse correction (smooth, no coupling artifacts).
        // Phantom + SAS take over for fine hold below threshold.
        // Without a pilot, game gyros are passive — phantom must handle all errors.
        const float GyroHandoffRad = 0.087f; // ~5 degrees
        bool gyroCoarseMode = !aero.SuppressPhantomTorque
            && !aero.HarnessControlsAttitude
            && aero._gyroCache.Count > 0
            && aero.Data.Has<TargetControlData>()
            && aero.EulerError.LengthSquared() > GyroHandoffRad * GyroHandoffRad;

        aero.LastAeroTorque = Vector3.Zero; aero.LastSurfaceTorque = Vector3.Zero;
        Vector3 appliedF = Vector3.Zero, appliedT = Vector3.Zero;   // (the structure's air loads: StructureServer)
        aero.AeroForcesApplied = aero.HasResult && mass > 0f && !AeroEntryFx.TestForcesOn;
        if (aero.HasResult && mass > 0f && !AeroEntryFx.TestForcesOn)
        {
            // (the world's drag / lift multipliers - AeroSettings, server - on the model's result: 1 / 1 is the model as is)
            Vector3 forceLocal = aero.LastResult.Force, torqueLocal = aero.LastResult.Torque;
            if (AeroTuning.DragMultiplier != 1f || AeroTuning.LiftMultiplier != 1f)
                AeroTuning.Scale(ref forceLocal, ref torqueLocal, WorldTransform.TransformDirectionInv(airVel, wt));
            Vector3 worldForce = WorldTransform.TransformDirection(forceLocal, wt);
            Vector3 deltaV = worldForce * (dt / mass); // diagnostics only; the impulse path uses worldForce

            // Log first 120 frames of force application to diagnose launch acceleration
            if (aero._simFrameCount < 120 && aero._simFrameCount % 10 == 0)
            {
                float fDotV = aero.LastSpeed > 0.1f ? Vector3.Dot(worldForce, linVel / aero.LastSpeed) : 0f;
                Log.Default?.Info($"[AERO-SIM] f={aero._simFrameCount} v={aero.LastSpeed:F1} " +
                    $"|dV|={deltaV.Length():F4} |F|={worldForce.Length():F0} " +
                    $"FdotV={fDotV:F0} d={density:F4} " +
                    $"localF=({forceLocal.X:F0},{forceLocal.Y:F0},{forceLocal.Z:F0})");
            }

            Vector3 sasTorque = (ThrustTorque.Enabled || aero.SuppressPhantomTorque || gyroCoarseMode) ? Vector3.Zero : aero.SasTorque;   // (no phantom: ThrustTorque)
            Vector3 totalTorque = torqueLocal + sasTorque;
            // (the attitude control's feedforward: the airframe's own torque - not the block components' (control surfaces:
            //  they are actuators, deflected on purpose; fed forward, the aero-aware law had the gyros cancel every turn they
            //  helped with, 2026-10-12). The world's multipliers taken as they scale the whole.)
            float tuneScale = aero.LastResult.Torque.LengthSquared() > 1e-6f ? torqueLocal.Length() / aero.LastResult.Torque.Length() : 1f;
            aero.LastSurfaceTorque = aero.LastComponentTorque * tuneScale;
            aero.LastAeroTorque = torqueLocal - aero.LastSurfaceTorque;
            // Real physics path (VRage.Physics whitelisted since 2.4.0.77): writes through a
            // ref into component storage and uses the engine's own inertia math, instead of
            // PhysicsHack's boxed read-modify-write with a hand-rolled I^-1 tensor rotation.
            long tap = AeroCost.Start();
            AeroPhysics.ApplyForceAndTorque(aero.Entity, wt, worldForce, totalTorque, dt);
            AeroCost.Apply.Stop(tap);
            appliedF = forceLocal; appliedT = torqueLocal;
        }
        // the structure and the hull's heat (StructureServer.cs: a background evaluation a few times a second)
        if (aero.IsServerScene) aero.TickStructure(dt, appliedF, appliedT, WorldTransform.TransformDirectionInv(airVel, wt), AeroTuning.LiftMultiplier);
        if (aero.ApiWatched) aero.PublishApiTick(airVel, linVel, appliedF, appliedT, gravity, wt);   // (the public API's tick record: AeroGridComponent.Api.cs)

        // ── Thrust torque and attitude: AttitudeJob, after the game's gyros (ThrustTorque.Enabled) ──
        // ── Offset thrust (RCS) correction (the old system: phantom attitude torque; ThrustTorque.Enabled = false) ──
        if (!ThrustTorque.Enabled && aero._thrusterCache.Count > 0)
        {
            aero._clearedOverride = 0;
            float thrustMach = 0f;
            Vector3 velLocalHat = Vector3.Zero;
            Vector3 velLocal = WorldTransform.TransformDirectionInv(linVel, wt);
            if (aero.LastSpeed > 1f)
            {
                velLocalHat = velLocal / velLocal.Length();

                // Compute Mach from speed + atmosphere, independent of aero HasResult.
                // Without atmosphere model, use standard sea-level speed of sound (343 m/s).
                if (aero.HasResult)
                    thrustMach = (float)aero.LastResult.Mach;
                else
                    thrustMach = aero.LastSpeed / 343f;
            }

            // Pass aero torque for feedforward cancellation in attitude controller
            Vector3 aeroTorqueFF = aero.HasResult
                ? aero.LastResult.Torque + aero.SasTorque
                : Vector3.Zero;

            Vector3 gravLocal = WorldTransform.TransformDirectionInv(gravity, wt);
            // Read actual dampener state from grid entity (DampeningData tag = dampeners on)
            // Test harness forces dampeners on so per-thruster attitude control is active
            bool dampenersOn = aero.HarnessControlsAttitude || aero.SuppressPhantomTorque || aero.Data.Has<DampeningData>();

            long tth = AeroCost.Start();
            OffsetThrustJob.Execute(
                aero._thrusterCache,
                aero.Data,
                aero.Entity,
                wt,
                angVel,
                enableDampening: dampenersOn,
                mach: thrustMach,
                velocityLocalHat: velLocalHat,
                velocityLocal: velLocal,
                gravityLocal: gravLocal,
                mass: mass,
                targetAngVel: aero._lastGridAngVel,
                aeroTorqueLocal: aeroTorqueFF,
                skipOffsetLoop: false);
            AeroCost.Thrust.Stop(tth);

            // ── Phantom attitude + coupling cancellation ──
            // 1. Cancel ALL offset coupling (prevents asymmetric thrust from spinning grid)
            // 2. Apply attitude correction phantom (PD controller → torque)
            // The per-thruster overrides handle hover/dampening (real thrust, visual, fuel).
            // Attitude comes from phantom torque (clean, no cross-coupling artifacts).
            Vector3 couplingCancel = -OffsetThrustJob.NetOffsetCouplingTorque;
            Vector3 localAngVel2 = WorldTransform.TransformDirectionInv(angVel, wt);
            // Scale by inertia so gains are ship-size-independent
            // Kp_norm: desired angular accel per rad/s of target (rad/s²)
            // Kd_norm: desired angular accel per rad/s of spin (rad/s²)
            const float Kp_norm = 8.0f;   // 8 rad/s² per rad/s target
            const float Kd_norm = 12.0f;  // 12 rad/s² per rad/s spin (strong damping)
            // Approximate inertia from mass and grid extent
            // For a uniform box: I ≈ mass * L² / 6. Typical grid L ≈ 10m.
            // Better: use actual inverse inertia if available
            float approxInertia = mass * 12f; // rough: mass * (average_radius)²
            if (aero.LastInvInertia.LengthSquared() > 0f)
            {
                // Use actual inertia (inverse of inverse)
                float avgInvI = (Math.Abs(aero.LastInvInertia.X) + Math.Abs(aero.LastInvInertia.Y) + Math.Abs(aero.LastInvInertia.Z)) / 3f;
                if (avgInvI > 1e-10f) approxInertia = 1f / avgInvI;
            }
            Vector3 attTorque = (aero._lastGridAngVel * Kp_norm - localAngVel2 * Kd_norm) * approxInertia;
            const float MaxAttTorque = 20000000f;
            attTorque = new Vector3(
                Math.Clamp(attTorque.X, -MaxAttTorque, MaxAttTorque),
                Math.Clamp(attTorque.Y, -MaxAttTorque, MaxAttTorque),
                Math.Clamp(attTorque.Z, -MaxAttTorque, MaxAttTorque));
            // In gyro coarse mode, only apply coupling cancel (no phantom attitude).
            // Gyros handle coarse correction smoothly; phantom takes over for fine hold.
            Vector3 totalPhantom = gyroCoarseMode ? couplingCancel : couplingCancel + attTorque;
            if (!aero.SuppressPhantomTorque && totalPhantom.LengthSquared() > 1f)
                AeroPhysics.ApplyForceAndTorque(aero.Entity, wt, Vector3.Zero, totalPhantom, dt);
        }

        // ── Throttled drag-vs-Mach log ──
        if (AeroLog.Verbose && aero.HasResult && aero.LastSpeed > 50f && --_simDragLogCooldown <= 0)
        {
            _simDragLogCooldown = 60;
            var r = aero.LastResult;
            Log.Default?.Info(
                $"[AERO] M={r.Mach:F3} v={aero.LastSpeed:F1} q={r.DynamicPressure:F0} " +
                $"F=({r.Force.X:F0},{r.Force.Y:F0},{r.Force.Z:F0}) |F|={r.Force.Length():F0}");
        }

        // ── Update focus position for draw culling ──
        if (aero.LastSpeed > 50f)
            AeroGridComponent.DebugFocusPosition = wt.Position;

        // ── Commit stats ──
        AeroStats.CommitGrid(0f, aero.LastSpeed > 50f,
            aero._surface?.FaceCount ?? 0,
            aero._model?.Wings?.Count ?? 0,
            aero._components?.Count ?? 0,
            aero.LastSpeed,
            aero.HasResult ? (float)aero.LastResult.Mach : 0f);

        // ── Test harness (runs after all production physics) ──
        AeroTestHarness.Tick(aero, wt);

        // ── THROWAWAY: teleport-stepping feasibility spike ──
        AeroSpeedSpike.Tick(aero, wt);

        // ── Scripted flight test (synthetic stick, no teleporting after setup) ──
        AeroFlightTest.Tick(aero, wt);
    }
}
