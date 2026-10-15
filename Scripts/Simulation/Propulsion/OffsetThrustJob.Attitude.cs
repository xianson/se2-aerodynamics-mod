#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;

namespace AeroMod;

/// <summary>The thrusters' attitude and damping overrides (the per-thruster RCS law over the pilot's raw inputs).</summary>
public static partial class OffsetThrustJob
{
    /// <summary>
    /// Pure Bob RCS pattern with raw vanilla inputs.
    ///
    /// Each thruster independently:
    ///   1. movement = Dot(-movementInput, forceDir) — player WASD
    ///   2. rotation = Dot(-rotationInput, torqueArm) — player mouse/roll
    ///   3. If movement == 0, linear damping: Dot(-velocity, forceDir)
    ///   4. If rotation == 0, angular damping: Dot(-angVel, torqueArm)
    ///   5. throttle = rotation * limiter + movement * (1 - limiter)
    /// </summary>
    private static void ApplyAttitudeViaOverrides(
        List<ThrusterInfo> thrusters,
        Vector3 comLocal,
        bool enableDampening,
        Vector3 localAngVel,
        Vector3 velocityLocal,
        Vector3 playerMovement,
        Vector3 playerRotation,
        Vector3 targetAngVel = default)
    {
        int n = thrusters.Count;
        _traceNetAttitudeTorque = Vector3.Zero;
        _netOverrideThrust = Vector3.Zero;

        bool hasPlayerMove = playerMovement.LengthSquared() > 0.0001f;
        bool hasPlayerRot = playerRotation.LengthSquared() > 0.0001f;
        bool hasAngVel = localAngVel.Length() > AngularDampeningThreshold;
        bool hasLinVel = velocityLocal.Length() > LinearDampeningThreshold;

        // Throttled debug: log inputs every 2 seconds
        if (_traceActive && _traceFrame % 120 == 0)
        {
            Log.Default?.Info($"[AERO-INPUT] damp={enableDampening}" +
                $" move=({playerMovement.X:F2},{playerMovement.Y:F2},{playerMovement.Z:F2})" +
                $" rot=({playerRotation.X:F2},{playerRotation.Y:F2},{playerRotation.Z:F2})" +
                $" |w|={localAngVel.Length():F4} |v|={velocityLocal.Length():F2}");
        }

        // If dampeners off and no player input, nothing to do
        if (!enableDampening && !hasPlayerMove && !hasPlayerRot) return;

        int overrideCount = 0;
        Vector3 producedTorque = Vector3.Zero;

        for (int i = 0; i < n; i++)
        {
            var t = thrusters[i];
            Vector3 forceDir = -t.ThrustDirection;
            Vector3 r = t.GridLocalPosition - comLocal;
            Vector3 torqueArm = Vector3.Cross(r, forceDir);
            float arm = torqueArm.Length();

            // Live-read attitude fraction from terminal slider (falls back to cached value)
            float limiter = t.Settings != null ? t.Settings.AttitudeFraction : t.AttitudeFraction;

            float movement = 0f;
            float rotation = 0f;
            bool movementFromPlayer = false;

            // ── Step 1: movement from player WASD ──
            if (hasPlayerMove)
            {
                movement = Math.Clamp(Vector3.Dot(-playerMovement, forceDir), -1f, 1f);
                if (movement != 0f) movementFromPlayer = true;
            }

            // ── Step 2: rotation from player mouse/roll ──
            if (hasPlayerRot && arm > 0.001f)
                rotation = Math.Clamp(Vector3.Dot(-playerRotation, torqueArm), -1f, 1f);

            if (enableDampening)
            {
                // ── Step 3: if THIS thruster's movement == 0, linear damping ──
                if (movement == 0f && hasLinVel)
                {
                    float rawMov = Vector3.Dot(-velocityLocal, forceDir);
                    movement = Math.Abs(rawMov) < LinearDampeningThreshold ? 0f
                        : Math.Clamp(rawMov, -1f, 1f);
                }

                // ── Step 4: if THIS thruster's rotation == 0, attitude command + angular damping ──
                // SE1 Bob pattern: attitude command and rate-nulling are INDEPENDENT per-thruster.
                // Combine: project attitude command onto torqueArm, PLUS rate-nulling damping.
                // This avoids the cross-axis coupling that happens when D-term is in the command.
                if (rotation == 0f && arm > 0.001f)
                {
                    float attRot = 0f;
                    float dampRot = 0f;

                    // Attitude command (P-only from harness/attitude hold)
                    if (targetAngVel.LengthSquared() > AngularDampeningThreshold * AngularDampeningThreshold)
                    {
                        float rawAtt = Vector3.Dot(targetAngVel, torqueArm);
                        attRot = Math.Abs(rawAtt) < AngularDampeningThreshold ? 0f
                            : Math.Clamp(rawAtt, -1f, 1f);
                    }

                    // Rate-nulling damping (always active, independent of attitude command)
                    if (hasAngVel)
                    {
                        float rawDamp = Vector3.Dot(-localAngVel, torqueArm);
                        dampRot = Math.Abs(rawDamp) < AngularDampeningThreshold ? 0f
                            : Math.Clamp(rawDamp, -1f, 1f);
                    }

                    // Blend: attitude drives toward target, damping prevents overshoot
                    rotation = Math.Clamp(attRot + dampRot, -1f, 1f);
                }
            }

            // ── Step 5: Bob blend ──
            // When only one axis has demand, give it full authority.
            // Only blend when both translation and rotation want the thruster.
            float throttle;
            if (rotation <= 0f)
                throttle = Math.Clamp(movement, 0f, 1f);
            else if (movement <= 0f)
                throttle = Math.Clamp(rotation, 0f, 1f);
            else
                throttle = Math.Clamp(rotation * limiter + movement * (1f - limiter), 0f, 1f);

            // Both non-positive → release to vanilla (override=0)
            float overrideValue;
            if (movement <= 0f && rotation <= 0f)
                overrideValue = 0f;
            else
                overrideValue = throttle > 0.0001f ? throttle : 0.0001f;

            // Temporary debug: log first 4 thrusters every 60 frames
            if (i < 4 && _traceFrame % 60 == 1)
            {
                Log.Default?.Info($"[BOB-DBG] T{i} mov={movement:F4} rot={rotation:F4} thr={throttle:F4} ovr={overrideValue:F4}" +
                    $" arm={arm:F1} lim={limiter:F2}" +
                    $" tgtAV=({targetAngVel.X:F3},{targetAngVel.Y:F3},{targetAngVel.Z:F3})" +
                    $" locAV=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                    $" damp={enableDampening}");
            }

            // NOTE: Per-thruster ThrusterOverrideData tags intentionally NOT set here.
            // The collector recomputes OverriddenThrustData from per-thruster tags with wrong
            // direction mapping. Instead, we write OverriddenThrustData directly on the grid.
            overrideCount++;
            if (throttle > 0.001f)
            {
                producedTorque += torqueArm * (throttle * t.MaxPower);
                // Only accumulate damping/attitude thrust into OverriddenThrustData.
                // Player input thrust is already handled by ComputeThrust via ControlData.
                if (!movementFromPlayer)
                    _netOverrideThrust += forceDir * (throttle * t.MaxPower);
            }

            // Record debug state
            Vector3 torqueAxis = arm > 0.001f ? torqueArm / arm : Vector3.Zero;
            if (DebugStatesByGrid.TryGetValue(thrusters, out var attDebugStates) && i < attDebugStates.Count)
            {
                var dbg = attDebugStates[i];
                dbg.DTermComponent = rotation;
                dbg.PTermComponent = movement;
                dbg.TorqueAxis = torqueAxis;
                dbg.TorqueArm = arm;
                dbg.AttitudeOverride = throttle;
                attDebugStates[i] = dbg;
            }
        }
        _traceNetAttitudeTorque = producedTorque;

        // Dampening diagnostic: log once per second when dampeners are active and velocity is significant
        _dampDiagCooldown = Math.Max(0, _dampDiagCooldown - 1);
        if (enableDampening && hasLinVel && _dampDiagCooldown == 0)
        {
            int posOverrides = 0;
            int actualFiring = 0;
            float maxOvr = 0f;
            float totalActual = 0f;
            for (int j = 0; j < n; j++)
            {
                float ovr = Math.Max(0f, PhysicsHack.GetThrustOverride(thrusters[j].ThrusterEntity.Data));
                if (ovr > 0.001f) posOverrides++;
                if (ovr > maxOvr) maxOvr = ovr;
                float actual = GetActualThrust(thrusters[j].ThrusterComponent);
                if (actual > 0.01f) { actualFiring++; totalActual += actual; }
            }
            Log.Default?.Info($"[DAMP-DIAG] |v|={velocityLocal.Length():F1} vel=({velocityLocal.X:F1},{velocityLocal.Y:F1},{velocityLocal.Z:F1})" +
                $" damp={enableDampening} n={n} posOvr={posOverrides} maxOvr={maxOvr:F3}" +
                $" actualFiring={actualFiring} totalActualN={totalActual:F0}" +
                $" ovrThrust=({_netOverrideThrust.X:F0},{_netOverrideThrust.Y:F0},{_netOverrideThrust.Z:F0})" +
                $" |w|={localAngVel.Length():F4}");
            _dampDiagCooldown = 60;
        }

        // Trace logging
        if (_traceActive && _traceIndex >= 0 && _traceIndex < n)
        {
            var tt = thrusters[_traceIndex];
            Vector3 fd = -tt.ThrustDirection;
            Vector3 ta = Vector3.Cross(tt.GridLocalPosition - comLocal, fd);

            float mov = hasPlayerMove ? Math.Clamp(Vector3.Dot(-playerMovement, fd), -1f, 1f) : 0f;
            if (mov == 0f && hasLinVel) mov = Math.Clamp(Vector3.Dot(-velocityLocal, fd), -1f, 1f);
            float rot = hasPlayerRot ? Math.Clamp(Vector3.Dot(-playerRotation, ta), -1f, 1f) : 0f;
            if (rot == 0f && ta.Length() > 0.001f)
            {
                if (targetAngVel.LengthSquared() > AngularDampeningThreshold * AngularDampeningThreshold)
                    rot = Math.Clamp(Vector3.Dot(targetAngVel, ta), -1f, 1f);
                else if (hasAngVel)
                    rot = Math.Clamp(Vector3.Dot(-localAngVel, ta), -1f, 1f);
            }
            float trLim = tt.Settings != null ? tt.Settings.AttitudeFraction : tt.AttitudeFraction;
            float trThrottle = Math.Clamp(rot * trLim + mov * (1f - trLim), 0f, 1f);

            Log.Default?.Info($"[AERO-TRACE] ATT f={_traceFrame}" +
                $" vel=({velocityLocal.X:F1},{velocityLocal.Y:F1},{velocityLocal.Z:F1})" +
                $" angVel=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                $" thr[{_traceIndex}] mov={mov:F3} rot={rot:F3}" +
                $" thr={trThrottle:F3} lim={trLim:F2} overrides={overrideCount}");
        }
    }
}
