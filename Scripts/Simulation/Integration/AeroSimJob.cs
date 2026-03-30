#pragma warning disable
using System;
using Keen.Game2.Simulation;
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

            // Merge SAS torque with aero torque (both in local frame)
            Vector3 aeroTorque = aero.LastResult.Torque;
            if (aero.DiagActive)
            {
                if (aero._diagPhase >= 5)
                {
                    // Phase 5+: only CS component torque + force, no body aero
                    aeroTorque = aero.LastComponentTorque;
                    Vector3 csWorldForce = WorldTransform.TransformDirection(aero.LastComponentForce, wt);
                    deltaV = csWorldForce * (dt / mass);
                }
                else
                {
                    aeroTorque = Vector3.Zero;
                    deltaV = Vector3.Zero;
                }
            }
            Vector3 totalTorque = aeroTorque + aero.SasTorque;

            // ── Diagnostic probe: before/after angular velocity ──
            bool probeLog = aero.DiagActive && totalTorque.LengthSquared() > 1f;
            Vector3 preAngWorld = Vector3.Zero;
            if (probeLog)
                PhysicsHack.TryGetVelocity(aero.Data, out _, out preAngWorld);

            PhysicsHack.ApplyDeltaVAndTorque(aero.Data, deltaV, totalTorque, dt, wt.Orientation);

            if (probeLog)
            {
                PhysicsHack.TryGetVelocity(aero.Data, out _, out Vector3 postAngWorld);
                Vector3 preLocal = WorldTransform.TransformDirectionInv(preAngWorld, wt);
                Vector3 postLocal = WorldTransform.TransformDirectionInv(postAngWorld, wt);
                Vector3 dLocal = postLocal - preLocal;
                Log.Default?.Info($"[PROBE-APPLY] torqueLocal=({totalTorque.X:F0},{totalTorque.Y:F0},{totalTorque.Z:F0}) " +
                    $"dLocal=({dLocal.X:F6},{dLocal.Y:F6},{dLocal.Z:F6})");
            }
        }

        // ── Thruster cache rebuild ──
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
            Vector3 aeroTorqueFF = (aero.HasResult && !aero.DiagActive)
                ? aero.LastResult.Torque + aero.SasTorque
                : Vector3.Zero;

            Vector3 gravLocal = WorldTransform.TransformDirectionInv(gravity, wt);
            OffsetThrustJob.Execute(
                aero._thrusterCache,
                aero.Data,
                wt,
                angVel,
                enableDampening: true,
                mach: thrustMach,
                velocityLocalHat: velLocalHat,
                velocityLocal: velLocal,
                gravityLocal: gravLocal,
                mass: mass,
                targetAngVel: aero._lastGridAngVel,
                aeroTorqueLocal: aeroTorqueFF);
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
        if (aero.DiagActive || aero.LastSpeed > 50f)
            AeroGridComponent.DebugFocusPosition = wt.Position;

        // ── Commit stats ──
        AeroStats.CommitGrid(0f, aero.DiagActive || aero.LastSpeed > 50f,
            aero._surface?.FaceCount ?? 0,
            aero._model?.Wings?.Count ?? 0,
            aero._components?.Count ?? 0,
            aero.LastSpeed,
            aero.HasResult ? (float)aero.LastResult.Mach : 0f);
    }
}
