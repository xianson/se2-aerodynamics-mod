using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.OWT;

#pragma warning disable
namespace AeroMod;

// The attitude the grid steers to (ThrustTorque reads AttitudeTarget) and the unpiloted attitude hold (SAS torque).
public partial class AeroGridComponent
{
    /// <summary>
    /// The orientation the thrusters steer to (ThrustTorque): the pilot's target (TargetControlData, the one the
    /// game's gyros steer to); unpiloted with dampeners on, the orientation it was left at; else none. A rate
    /// command without a target (AngularControlData input) is followed by the game's gyros: no target here.
    /// </summary>
    internal bool AttitudeTarget(in WorldTransform wt, out Quaternion target, out string mode)
    {
        target = wt.Orientation;
        if (HarnessControlsAttitude) { mode = "harness"; return false; }
        if (--_subPartCheck <= 0)
        {
            _subPartCheck = 120;
            try { _isSubPart = PhysicsHack.IsSubPart(Entity, LastMass); } catch { _isSubPart = false; }
        }
        if (_isSubPart) { _rcsHoldValid = false; mode = "sub-part"; return false; }
        if (TestTurnGen != 0 && IsServerScene && Data.TryGet<TargetControlData>(out var tt))
        {
            // DEV (harness): the pilot's target set as the reticle sets it - the orientation when TestTurnGen last changed,
            // turned by TestTurnPitch / Yaw / Roll (deg, grid-local X / Y / Z) - written where the cockpit writes it
            if (_testTurnGen != TestTurnGen) { _testTurnGen = TestTurnGen; _testTurnBase = wt.Orientation; }
            const float D2R = MathF.PI / 180f;
            target = Quaternion.Normalize(_testTurnBase * Quaternion.CreateFromAxisAngle(Vector3.UnitX, TestTurnPitch * D2R)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, TestTurnYaw * D2R) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, TestTurnRoll * D2R));
            tt.TargetOrientation = target; Data.Set(tt);
            _rcsHold = target; _rcsHoldValid = true; mode = "pilot target"; return true;
        }
        if (Data.TryGet<TargetControlData>(out var tc) && tc.TargetOrientation.IsValidAndRotationIsNormalized())
        {
            target = tc.TargetOrientation; _rcsHold = target; _rcsHoldValid = true; mode = "pilot target"; return true;
        }
        if ((Data.TryGet<AngularControlData>(out var ac) && ac.TargetAngularVelocity.LengthSquared() > 1e-6f)
            || (Data.TryGet<ControlData>(out var cd) && cd.Rotation.LengthSquared() > 1e-4f))
        {
            _rcsHoldValid = false; mode = "rate input"; return false;
        }
        if (Data.Has<DampeningData>())
        {
            // UNPILOTED, dampeners on: as the game's gyros (their target rate zero) - the rotation damped, the thrusters'
            // own offset torque cancelled, NO remembered attitude. It held the attitude the ship was left at: a pilot leaving
            // mid-turn left their last target and the ship crept back to it for ~15 s, thrusters lit, against the gyros
            // braking it; a rate pilot letting go got snapped back past where the gyros stopped (audit 2026-10-10)
            _rcsHoldValid = false;
            target = wt.Orientation; mode = "damp"; return true;
        }
        _rcsHoldValid = false; mode = "free"; return false;
    }

    /// <summary>
    /// Attitude hold: when no pilot is present, hold the current orientation.
    /// Runs every frame before early returns so it works even without atmosphere.
    /// Sets _lastGridAngVel and SasTorque when no pilot input is detected.
    /// </summary>
    private void UpdateAttitudeHold(WorldTransform wt, Vector3 angularVelocity)
    {
        // Test harness drives attitude directly — don't overwrite its values
        if (HarnessControlsAttitude) return;

        // Check if a pilot is providing input
        bool hasTargetData = Data.TryGet<TargetControlData>(out _);
        bool hasAngularData = Data.TryGet<AngularControlData>(out _);

        // AngularControlData persists on entity even without a pilot (stale ECS component).
        // Check if there's actual nonzero input, not just component existence.
        bool hasRealInput = hasTargetData;
        if (!hasRealInput && hasAngularData)
        {
            var angData = Data.Get<AngularControlData>();
            hasRealInput = angData.TargetAngularVelocity.LengthSquared() > 0.0001f;
        }

        if (hasRealInput)
        {
            _holdOrientationValid = false;
            return;
        }

        // No pilot — hold orientation
        Quaternion gridOrientation = wt.Orientation;
        if (!_holdOrientationValid)
        {
            _holdOrientation = gridOrientation;
            _holdOrientationValid = true;
            Log.Default?.Info("[AERO-HOLD] Captured hold orientation (no pilot)");
        }
        else
        {
            // Re-capture if error is too large (stale target from long ago)
            Quaternion checkErr = Quaternion.Inverse(gridOrientation) * _holdOrientation;
            Vector3 checkEuler = checkErr.ConvertToEuler();
            if (checkEuler.LengthSquared() > 1f) // > ~1 radian total error
            {
                _holdOrientation = gridOrientation;
                if (AeroLog.Verbose) Log.Default?.Info("[AERO-HOLD] Re-captured hold orientation (error too large)");   // (tumbling debris: lines/s)
            }
        }

        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, wt);

        Quaternion errorQuat = Quaternion.Inverse(gridOrientation) * _holdOrientation;
        Vector3 eulerError = errorQuat.ConvertToEuler();
        EulerError = eulerError;

        // Output targetAngVel in rad/s — the per-thruster system uses
        // dot(targetAngVel, torqueVec) where torqueVec = Cross(r, forceDir)
        // (unnormalized, arm length included). This matches SE1 RealRCS pattern:
        // thrusters further from CoM naturally get more authority.
        const float HoldKp = 3.0f;   // rad/s per rad error
        const float HoldKd = 1.5f;   // damping
        const float HoldMax = 2.0f;  // max command rad/s

        const float HoldMaxEuler = 1.0f;  // ~57 degrees max euler contribution
        Vector3 holdEulerClamped = new Vector3(
            Math.Clamp(eulerError.X, -HoldMaxEuler, HoldMaxEuler),
            Math.Clamp(eulerError.Y, -HoldMaxEuler, HoldMaxEuler),
            Math.Clamp(eulerError.Z, -HoldMaxEuler, HoldMaxEuler));

        Vector3 attitudeCmd = holdEulerClamped * HoldKp - localAngVel * HoldKd;
        _lastGridAngVel = new Vector3(
            Math.Clamp(attitudeCmd.X, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Y, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Z, -HoldMax, HoldMax));

        if (OffsetThrustJob.Verbose && _simFrameCount % 120 == 0)
            Log.Default?.Info($"[AERO-HOLD] euler=({eulerError.X:F4},{eulerError.Y:F4},{eulerError.Z:F4})" +
                $" angVel=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                $" cmd=({_lastGridAngVel.X:F4},{_lastGridAngVel.Y:F4},{_lastGridAngVel.Z:F4})");

        // SAS torque for the hidden stability system
        {
            const float SasDamping = 2000000f;
            const float SasAttitude = 1000000f;
            const float SasMaxTorque = 10000000f;

            Vector3 sasDamp = -localAngVel * SasDamping;
            Vector3 clampedEuler = new Vector3(
                MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.X)),
                MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.Y)),
                MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.Z)));
            Vector3 sasAtt = clampedEuler * SasAttitude;

            SasTorque = sasDamp + sasAtt;
            SasTorque = new Vector3(
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.X)),
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.Y)),
                MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, SasTorque.Z)));
        }
    }
}
