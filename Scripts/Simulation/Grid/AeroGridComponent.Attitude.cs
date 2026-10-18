using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

// The attitude the grid steers to (ThrustTorque reads AttitudeTarget) and the unpiloted rotation damping command.
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
    /// Unpiloted: damp the rotation, hold no attitude (AttitudeTarget's rule) - the rate the control surfaces are asked for
    /// (_lastGridAngVel, their HOLD command: AeroGridComponent.Surfaces). Runs every frame before early returns, so it works
    /// even without atmosphere.
    /// </summary>
    private void UpdateAttitudeHold(WorldTransform wt, Vector3 angularVelocity)
    {
        // A pilot giving input: the surfaces' mixer sets the rate (UpdateControlSurfaceInputs)
        if (Data.Has<TargetControlData>()) return;
        // (AngularControlData persists on the entity without a pilot - a stale component: only real input counts)
        if (Data.TryGet<AngularControlData>(out var angData) && angData.TargetAngularVelocity.LengthSquared() > 0.0001f) return;

        // No pilot: the rotation damped, NO remembered attitude - as AttitudeTarget (2026-10-10). (2026-10-15: this still
        // captured the orientation the ship was left at and steered _lastGridAngVel back to it - the control surfaces'
        // HOLD command - up to ~1 rad away, dampeners on or off, with an Info log line on every capture.)
        Vector3 localAngVel = WorldTransform.TransformDirectionInv(angularVelocity, wt);
        const float HoldKd = 1.5f;   // damping
        const float HoldMax = 2.0f;  // max command rad/s
        Vector3 attitudeCmd = -localAngVel * HoldKd;
        _lastGridAngVel = new Vector3(
            Math.Clamp(attitudeCmd.X, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Y, -HoldMax, HoldMax),
            Math.Clamp(attitudeCmd.Z, -HoldMax, HoldMax));
    }
}
