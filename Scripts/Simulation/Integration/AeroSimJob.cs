#pragma warning disable
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
            if (PitchLog && aero.IsServerScene && aero.Data.Has<TargetControlData>() && aero._simFrameCount % 6 == 0)
            {
                PhysicsHack.TryGetVelocity(aero.Data, out _, out var w2);
                var wl = WorldTransform.TransformDirectionInv(w2, wt); var r = aero.LastThrust;
                Log.Default?.Info($"[AERO-PITCH] v={aero.LastSpeed:F0} M={(aero.HasResult ? aero.LastResult.Mach : 0):F2} w=({wl.X:F3},{wl.Y:F3},{wl.Z:F3}) err=({r.Error.X * 57.3f:F2},{r.Error.Y * 57.3f:F2},{r.Error.Z * 57.3f:F2})deg"
                    + $" aeroT=({aero.LastAeroTorque.X / 1000:F0},{aero.LastAeroTorque.Y / 1000:F0},{aero.LastAeroTorque.Z / 1000:F0})kNm gyro=({r.GyroTorque.X / 1000:F0},{r.GyroTorque.Y / 1000:F0},{r.GyroTorque.Z / 1000:F0})kNm"
                    + $" rcs=({r.RcsTorque.X / 1000:F0},{r.RcsTorque.Y / 1000:F0},{r.RcsTorque.Z / 1000:F0})kNm undone=({r.GameGyroUndone.X:F4},{r.GameGyroUndone.Y:F4},{r.GameGyroUndone.Z:F4}) aware={(r.Aware ? 1 : 0)} mode={r.Mode}");
            }
        }
        finally { AeroCost.ExitSim(); }
        AeroCost.Thrust.Stop(tth2);
    }

    [OnAeroSim]
    [MustHave(typeof(AeroGridComponent))]
    private static void AeroSimJob(AeroGridComponent aero, WorldTransform wt)
    {
        long t0 = AeroCost.Start();
        AeroCost.EnterSim();
        try { AeroSimJobCore(aero, wt); } finally { AeroCost.ExitSim(); }
        AeroCost.Sim.Stop(t0);
        AeroCost.Watch(aero);
        if (aero.IsServerScene) Interlocked.Increment(ref AeroCost.SimServer); else Interlocked.Increment(ref AeroCost.SimClient);
        AeroCost.WatchThrust(aero);
        AeroCost.MaybeLog();
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

        // Zero angular velocity on first frame to clear saved-world spin
        if (aero._simFrameCount == 1)
            PhysicsHack.TryZeroAngularVelocity(aero.Data);

        // ── Read physics state ──
        PhysicsHack.TryGetVelocity(aero.Data, out Vector3 linVel, out Vector3 angVel);
        PhysicsHack.TryGetMassProperties(aero.Data, out float mass, out Vector3 com);

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

        // ── Ground height (async raycast) ──
        if (aero.IsServerScene)
            PhysicsHack.InitGroundSystem(aero.Entity);   // (cheap when unchanged: the server session's physics)

        Vector3 gravity = PhysicsHack.GetGravityDirection(aero.Data);
        aero.InGravity = gravity.LengthSquared() > 1e-4f;
        aero.GroundHeight = aero.IsServerScene ? PhysicsHack.GetGroundDistance(aero.Ground, wt.Position, gravity, aero.Entity) : -1f;   // (rays in the server's world only)
        aero.GroundUpLocal = aero.InGravity ? -Vector3.Normalize(WorldTransform.TransformDirectionInv(gravity, wt)) : Vector3.Zero;

        // ── Cache physics state for draw job ──
        aero.LastLinVel = linVel;
        aero.LastAngVel = angVel;
        aero.LastCoM = com;
        aero.LastMass = mass;
        aero.LastDensity = density;
        aero.LastSpeed = linVel.Length();
        if (PhysicsHack.TryGetInertiaData(aero.Data, out var invI, out var majorAxisRot))
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
        long tco = AeroCost.Start();
        aero.CachedPriority = aero.BuildPriority;
        AeroFrameBudget.Touch();
        int every = aero.ComputeInterval;
        bool due = every <= 1 || !aero.HasResult || ((aero._simFrameCount + aero.LodPhase) % every) == 0 || aero.BudgetSkips > 0;
        // the frame budget: once spent, due grids wait (not piloted / fast ones, nor one that has waited MaxSkips)
        if (due && every > 1 && aero.HasResult && AeroFrameBudget.Spent && aero.BudgetSkips < AeroFrameBudget.MaxSkips)
        {
            aero.BudgetSkips++;
            due = false;
            System.Threading.Interlocked.Increment(ref AeroFrameBudget.SkippedThisSecond);
        }
        if (due)
        {
            aero.BudgetSkips = 0;
            if (AeroEntryFx.TestForcesOn)   // (TEST: the forces at the test airflow, measured not applied)
                aero.TryCompute(wt, MathF.Max(density, 1.2f), AeroEntryFx.TestVelocityWorld(wt), angVel, com, aero.GroundHeight);
            else
                aero.TryCompute(wt, density, linVel, angVel, com, aero.GroundHeight);
            System.Threading.Interlocked.Increment(ref AeroFrameBudget.ComputedThisSecond);
        }
        else
            aero.SkipCompute(wt, angVel);   // (the last forces, in the grid's frame, are applied again below)
        AeroFrameBudget.Add(System.Diagnostics.Stopwatch.GetTimestamp() - tco);
        AeroCost.Compute.Stop(tco);
        AeroEntryFx.Flow(aero, wt, linVel, density);
        AeroEntryFx.Server(aero, wt, linVel, density, 1f / 60f);
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

        aero.LastAeroTorque = Vector3.Zero;
        if (aero.HasResult && mass > 0f && !AeroEntryFx.TestForcesOn)
        {
            // (the world's drag / lift multipliers - AeroSettings, server - on the model's result: 1 / 1 is the model as is)
            Vector3 forceLocal = aero.LastResult.Force, torqueLocal = aero.LastResult.Torque;
            if (AeroTuning.DragMultiplier != 1f || AeroTuning.LiftMultiplier != 1f)
                AeroTuning.Scale(ref forceLocal, ref torqueLocal, WorldTransform.TransformDirectionInv(linVel, wt));
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
            aero.LastAeroTorque = torqueLocal;   // (the attitude control's feedforward: the very torque applied)
            // Real physics path (VRage.Physics whitelisted since 2.4.0.77): writes through a
            // ref into component storage and uses the engine's own inertia math, instead of
            // PhysicsHack's boxed read-modify-write with a hand-rolled I^-1 tensor rotation.
            long tap = AeroCost.Start();
            AeroPhysics.ApplyForceAndTorque(aero.Entity, wt, worldForce, totalTorque, dt);
            AeroCost.Apply.Stop(tap);
        }

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
