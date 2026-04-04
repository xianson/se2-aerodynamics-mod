#pragma warning disable
using System;
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

    [OnAeroSim]
    [MustHave(typeof(AeroGridComponent))]
    private static void AeroSimJob(AeroGridComponent aero, WorldTransform wt)
    {
        if (!aero._initialized) return;

        // Test harness: skip frozen grids
        if (AeroTestHarness.ShouldSkipGrid(aero))
        {
            PhysicsHack.TrySetVelocity(aero.Data, Vector3.Zero, Vector3.Zero);
            return;
        }

        aero._simFrameCount++;
        AeroStats.BeginGrid();

        // Zero angular velocity on first frame to clear saved-world spin
        if (aero._simFrameCount == 1)
            PhysicsHack.TryZeroAngularVelocity(aero.Data);

        // ── Read physics state ──
        PhysicsHack.TryGetVelocity(aero.Data, out Vector3 linVel, out Vector3 angVel);
        PhysicsHack.TryGetMassProperties(aero.Data, out float mass, out Vector3 com);

        float density = 0f;
        if (aero.Data.TryGet<AirData>(out var air))
            density = air.Density;

        // ── Ground height (async raycast) ──
        if (!PhysicsHack.GroundSystemReady)
            PhysicsHack.InitGroundSystem(aero.Entity.Scene);

        Vector3 gravity = PhysicsHack.GetGravityDirection(aero.Data);
        aero.GroundHeight = PhysicsHack.GetGroundDistance(wt.Position, gravity);

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

        // ── Thruster cache rebuild (before TryCompute so Phase 8 sees thrusters) ──
        if (aero._thrusterCacheDirty)
        {
            if (aero._blockSize <= 0)
                aero._blockSize = aero.DetectBlockSize();
            if (aero._blockSize > 0)
            {
                OffsetThrustJob.RebuildThrusterCache(aero._octree, aero._blockSize, aero._thrusterCache, aero.Entity);
                aero._thrusterCacheDirty = false;
            }
        }

        // ── Gyro cache rebuild ──
        if (aero._gyroCacheDirty)
        {
            OffsetThrustJob.RebuildGyroCache(aero.Entity, aero._gyroCache);
            aero._gyroCacheDirty = false;
        }

        // ── Clear stale ThrustOverride values on first frame (persisted from save) ──
        if (aero._simFrameCount == 1 && aero._thrusterCache.Count > 0)
        {
            for (int i = 0; i < aero._thrusterCache.Count; i++)
                OffsetThrustJob.ForceOverride(aero._thrusterCache[i].ThrusterComponent, 0f);
        }

        // ── Aero computation ──
        aero.TryCompute(wt, density, linVel, angVel, com, aero.GroundHeight);

        if (aero._simFrameCount <= 5 && aero.LastSpeed > 1f)
        {
            Log.Default?.Info($"[AERO-SIM] EARLY f={aero._simFrameCount} v={aero.LastSpeed:F1} " +
                $"d={density:F4} mass={mass:F0} hasResult={aero.HasResult} " +
                $"angVel=({angVel.X:F3},{angVel.Y:F3},{angVel.Z:F3})");
        }

        // ── Apply aero forces + torques ──
        float dt = 1f / 60f;

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

        if (aero.HasResult && mass > 0f)
        {
            Vector3 worldForce = WorldTransform.TransformDirection(aero.LastResult.Force, wt);
            Vector3 deltaV = worldForce * (dt / mass);

            // Log first 120 frames of force application to diagnose launch acceleration
            if (aero._simFrameCount < 120 && aero._simFrameCount % 10 == 0)
            {
                float fDotV = aero.LastSpeed > 0.1f ? Vector3.Dot(worldForce, linVel / aero.LastSpeed) : 0f;
                Log.Default?.Info($"[AERO-SIM] f={aero._simFrameCount} v={aero.LastSpeed:F1} " +
                    $"|dV|={deltaV.Length():F4} |F|={worldForce.Length():F0} " +
                    $"FdotV={fDotV:F0} d={density:F4} " +
                    $"localF=({aero.LastResult.Force.X:F0},{aero.LastResult.Force.Y:F0},{aero.LastResult.Force.Z:F0})");
            }

            Vector3 sasTorque = (aero.SuppressPhantomTorque || gyroCoarseMode) ? Vector3.Zero : aero.SasTorque;
            Vector3 totalTorque = aero.LastResult.Torque + sasTorque;

            PhysicsHack.ApplyDeltaVAndTorque(aero.Data, deltaV, totalTorque, dt, wt.Orientation);
        }

        // ── Offset thrust (RCS) correction ──
        if (aero._thrusterCache.Count > 0)
        {
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

            OffsetThrustJob.Execute(
                aero._thrusterCache,
                aero.Data,
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
                PhysicsHack.ApplyDeltaVAndTorque(aero.Data, Vector3.Zero, totalPhantom, dt, wt.Orientation);
        }

        // ── Throttled drag-vs-Mach log ──
        if (aero.HasResult && aero.LastSpeed > 50f && --_simDragLogCooldown <= 0)
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
    }
}
