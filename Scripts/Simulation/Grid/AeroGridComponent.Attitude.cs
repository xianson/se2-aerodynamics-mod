using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
        if (_isSubPart) { mode = "sub-part"; return false; }
        if (TestTurnGen != 0 && IsServerScene && Data.TryGet<TargetControlData>(out var tt))
        {
            // DEV (harness): the pilot's target set as the reticle sets it - the orientation when TestTurnGen last changed,
            // turned by TestTurnPitch / Yaw / Roll (deg, grid-local X / Y / Z) - written where the cockpit writes it
            if (_testTurnGen != TestTurnGen) { _testTurnGen = TestTurnGen; _testTurnBase = wt.Orientation; }
            const float D2R = MathF.PI / 180f;
            target = Quaternion.Normalize(_testTurnBase * Quaternion.CreateFromAxisAngle(Vector3.UnitX, TestTurnPitch * D2R)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, TestTurnYaw * D2R) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, TestTurnRoll * D2R));
            tt.TargetOrientation = target; Data.Set(tt);
            mode = "pilot target"; return true;
        }
        if (Data.TryGet<TargetControlData>(out var tc) && tc.TargetOrientation.IsValidAndRotationIsNormalized())
        {
            target = tc.TargetOrientation; mode = "pilot target"; return true;
        }
        if ((Data.TryGet<AngularControlData>(out var ac) && ac.TargetAngularVelocity.LengthSquared() > 1e-6f)
            || (Data.TryGet<ControlData>(out var cd) && cd.Rotation.LengthSquared() > 1e-4f))
        {
            mode = "rate input"; return false;
        }
        if (Data.Has<DampeningData>())
        {
            // UNPILOTED, dampeners on: as the game's gyros (their target rate zero) - the rotation damped, the thrusters'
            // own offset torque cancelled, NO remembered attitude. It held the attitude the ship was left at: a pilot leaving
            // mid-turn left their last target and the ship crept back to it for ~15 s, thrusters lit, against the gyros
            // braking it; a rate pilot letting go got snapped back past where the gyros stopped (audit 2026-10-10)
            target = wt.Orientation; mode = "damp"; return true;
        }
        mode = "free"; return false;
    }

    /// <summary>
    /// Unpiloted: damp the rotation, hold no attitude (AttitudeTarget's rule).
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

        // No pilot: the rotation damped, NO remembered attitude - as AttitudeTarget (2026-10-10). (2026-10-15: this still
        // captured the orientation the ship was left at and steered _lastGridAngVel back to it - the control surfaces'
        // HOLD command - up to ~1 rad away, dampeners on or off, with an Info log line on every capture.)
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, wt);
        Vector3 eulerError = Vector3.Zero;
        EulerError = eulerError;

        // Output targetAngVel in rad/s — the per-thruster system uses
        // dot(targetAngVel, torqueVec) where torqueVec = Cross(r, forceDir)
        // (unnormalized, arm length included). This matches SE1 RealRCS pattern:
        // thrusters further from CoM naturally get more authority.
        const float HoldKd = 1.5f;   // damping
        const float HoldMax = 2.0f;  // max command rad/s

        Vector3 attitudeCmd = -localAngVel * HoldKd;
        _lastGridAngVel = new Vector3(
            Math.Clamp(attitudeCmd.X, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Y, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Z, -HoldMax, HoldMax));

        if (OffsetThrustJob.Verbose && _simFrameCount % 120 == 0)
            Log.Default?.Info($"[AERO-HOLD] euler=({eulerError.X:F4},{eulerError.Y:F4},{eulerError.Z:F4})" +
                $" angVel=({localAngVel.X:F4},{localAngVel.Y:F4},{localAngVel.Z:F4})" +
                $" cmd=({_lastGridAngVel.X:F4},{_lastGridAngVel.Y:F4},{_lastGridAngVel.Z:F4})");

        // SAS torque for the hidden stability system
        SasTorque = SasTorqueFor(localAngVel, eulerError);
    }

    /// <summary>The hidden stability augmentation's torque (local, N m): damps the rotation and pulls toward the attitude
    /// error (each axis of it clamped to 0.5 rad), each axis clamped to 1e7 N m. The hold and the reticle mixer share it.</summary>
    private static Vector3 SasTorqueFor(Vector3 localAngVel, Vector3 eulerError)
    {
        const float SasDamping = 2000000f;   // N·m per rad/s — oppose rotation
        const float SasAttitude = 1000000f;   // N·m per rad — track the target
        const float SasMaxTorque = 10000000f; // clamp per axis

        Vector3 sasDamp = -localAngVel * SasDamping;
        Vector3 clampedEuler = new Vector3(
            MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.X)),
            MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.Y)),
            MathF.Max(-0.5f, MathF.Min(0.5f, eulerError.Z)));
        Vector3 sasAtt = clampedEuler * SasAttitude;

        Vector3 t = sasDamp + sasAtt;
        return new Vector3(
            MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, t.X)),
            MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, t.Y)),
            MathF.Max(-SasMaxTorque, MathF.Min(SasMaxTorque, t.Z)));
    }
}
