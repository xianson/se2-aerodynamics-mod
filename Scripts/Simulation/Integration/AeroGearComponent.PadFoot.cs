#pragma warning disable
using System;
using Keen.VRage.Core;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Colliders;
using Keen.VRage.Physics.Constraints;
using Keen.VRage.Physics.Constraints.Arguments;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// THE PAD-FOOT GEARS (server; 2026-10-20, the user: "No lock, custom wheel model that is the landing gear, and constrained"): the
/// vanilla suspension's wheel IS the foot - a vanilla landing-gear mesh on a heavy wheel body (gen_padgear_defs.py). Once per wheel the
/// corner module makes (the build, every load, a new wheel):
///  - its COLLIDER: the corner module's sphere (WheelColliderComponent fits one to the foot's puck) swapped for a box of the foot's
///    bounds (IPhysics.SetBodyCollider; its mass kept, its inertia the box's) - the foot stands flat. PadBoxCollider off: the sphere;
///  - its ORIENTATION LOCK: a 6-DOF constraint, foot to ship, fixing only the rotation about the axle (PadLockLife: the wheel
///    constraint holds the other two; every linear axis free - the suspension slides). The foot stays level - under load, sliding at
///    speed (it slides, it does not roll), in the well. The axle's friction is held as a backup (the vanilla brakes write it only
///    when they change).
/// Retraction, airshock and the stiffness cap are the gear's own (AeroGearComponent.cs), unchanged.
/// </summary>
public partial class AeroGearComponent
{
    /// <summary>Harness: the foot's box collider (true) or the corner module's sphere (false) - new wheels only.</summary>
    public static bool PadBoxCollider = true;
    /// <summary>Harness: the 6-DOF axis the lock fixes (3 AngularX: the twist about its first axis, the axle; 4, 5 the others - for
    /// finding the engine's axis order in game). New locks only.</summary>
    public static int PadLockAxis = 3;
    /// <summary>Harness: no orientation lock (the axle friction alone).</summary>
    public static bool PadLockEnabled = true;
    public static int PadLocks, PadLocksLost, PadBoxes;

    readonly PadLockLife _lockLife = new();
    IPhysicsConstraint _padLock; Func<bool> _padLockDead; Entity _padLockGrid;
    Entity _boxWheel; IPhysicsCollider _box, _oldBox; Entity _oldBoxWheel;
    bool _padAxleHeld;
    string _padStatus = "";

    /// <summary>The gear's step, after its wheel constraint is known: the box and the lock for this wheel, the axle held.</summary>
    void PadFootStep(Entity wheel)
    {
        var foot = Variant?.Foot;
        if (foot == null) return;
        var physics = GridPhysics.Physics;
        if (physics == null) return;
        bool alive = wheel != null && GridPhysics.Alive(wheel.Data);

        // the box: once per wheel (an old box freed once its wheel is gone)
        if (_oldBox != null && (_oldBoxWheel == null || !GridPhysics.Alive(_oldBoxWheel.Data))) { _oldBox.Dispose(); _oldBox = null; _oldBoxWheel = null; }
        if (alive && PadBoxCollider && !ReferenceEquals(_boxWheel, wheel) && wheel.Data.Has<RigidBodyData>())
        {
            if (_box != null) { _oldBox = _box; _oldBoxWheel = _boxWheel; }
            float mass = wheel.Data.TryGet<RigidBodyMassProperties>(out var mp) && mp.Mass > 0f ? mp.Mass : foot.WheelMass;
            var box = physics.Colliders.AllocateBox(new BoxColliderArgs(foot.BoxCenter, foot.BoxHalf));
            box.SetMassProperties(box.ComputeMassProperties(MassBuildingParameters.FromMass(mass)));
            physics.SetBodyCollider(wheel.DEntity, box);
            _box = box; _boxWheel = wheel; PadBoxes++;
            Log.Default?.Info($"[AERO-GEAR] {_key} pad foot: box collider {foot.BoxHalf.X * 2:F2} x {foot.BoxHalf.Y * 2:F2} x {foot.BoxHalf.Z * 2:F2} m, {mass:F0} kg");
        }

        // the orientation lock
        var did = _lockLife.Step(alive ? wheel : null, _padLockDead != null && _padLockDead());
        if (!_lockLife.Has && _padLock != null) { _padLock = null; _padLockDead = null; _padLockGrid = null; PadLocksLost++; }   // (gone with its body)
        if (did == PadLockDo.Create && PadLockEnabled) _lockLife.Made(wheel, MakeLock(physics, wheel));

        // the axle held (backup): every step while the foot is on - the vanilla brakes write the friction only when they change
        if (_wc != null)
        {
            if (!_wc.FrictionEnabled) _wc.FrictionEnabled = true;
            if (_wc.MaxFrictionTorque != foot.AxleHoldTorque) _wc.MaxFrictionTorque = foot.AxleHoldTorque;
            _padAxleHeld = true;
        }
    }

    /// <summary>The 6-DOF lock: frames on the axle (row 1), the foot's up (row 2), at the foot's origin, in each body's frame now.</summary>
    bool MakeLock(IPhysics physics, Entity wheel)
    {
        var grid = Entity.GetTopLevelParent();
        if (grid == null || !GridPhysics.Alive(grid.Data) || !physics.HasBody(wheel.DEntity) || !physics.HasBody(grid.DEntity)) return false;
        var wwt = wheel.Data.GetWorldTransform(); var gwt = grid.Data.GetWorldTransform();
        Vector3 fwdW = Vector3.Transform(Vector3.Forward, wwt.Orientation), upW = Vector3.Transform(Vector3.Up, wwt.Orientation);
        var args = new SixDegreesOfFreedomConstraintArgs
        {
            PivotA = Matrix.CreateWorld(Vector3.Zero, Vector3.Forward, Vector3.Up),   // (the foot's own: row 1 its X, the axle)
            PivotB = Matrix.CreateWorld((Vector3)WorldTransform.TransformInv(wwt.Position, gwt), WorldTransform.TransformDirectionInv(fwdW, gwt), WorldTransform.TransformDirectionInv(upW, gwt)),
            ExcludeFromGroup = true,   // (as the wheel constraint)
        };
        var fixedAxes = PadLockLife.FixedAxes(PadLockAxis);
        for (int i = 0; i < 6; i++) if (fixedAxes[i]) args.SetAxisMode((ConstraintAxisIdentifier)i, ConstraintAxisMode.Fixed);
        _padLock = physics.AllocateConstraint(wheel.DEntity, grid.DEntity, args);   // (A the foot, constrained to B the ship)
        if (_padLock == null) { _padStatus = "lock refused"; return false; }
        _padLockDead = StructureTear.DestroyedOf(_padLock); _padLockGrid = grid; PadLocks++;
        _padStatus = "locked level";
        Log.Default?.Info($"[AERO-GEAR] {_key} pad foot: orientation lock on (axis {(ConstraintAxisIdentifier)PadLockAxis})");
        return true;
    }

    /// <summary>The block going: the lock destroyed while both bodies live (else it went with them).</summary>
    void PadFootRemoved()
    {
        bool dead = _padLockDead != null && _padLockDead();
        var wheel = _cm?.WheelEntity;
        bool bodies = _padLock != null && wheel != null && GridPhysics.Alive(wheel.Data) && _padLockGrid != null && GridPhysics.Alive(_padLockGrid.Data);
        if (_lockLife.Removing(bodies, dead) == PadLockDo.Destroy) GridPhysics.Physics?.DestroyConstraint(_padLock);
        _padLock = null; _padLockDead = null; _padLockGrid = null; _boxWheel = null;
    }

    /// <summary>The foot's turn rate relative to the ship in the foot's own frame (rad/s: X its Right - the axle the lock fixes by
    /// default -, Y its Up, Z its Backward) - the harness's lock-axis readout (WheelShockDev).</summary>
    internal bool FootRates(out Vector3 relLocal)
    {
        relLocal = Vector3.Zero;
        var wheel = _cm?.WheelEntity; var grid = Entity?.GetTopLevelParent();
        if (Variant?.Foot == null || wheel == null || grid == null || !GridPhysics.Alive(wheel.Data) || !GridPhysics.Alive(grid.Data)) return false;
        if (!GridPhysics.TryGetVelocity(wheel.Data, out _, out Vector3 wA) || !GridPhysics.TryGetVelocity(grid.Data, out _, out Vector3 gA)) return false;
        relLocal = Vector3.Transform(wA - gA, Quaternion.Inverse(wheel.Data.GetWorldTransform().Orientation));
        return true;
    }

    /// <summary>The foot's tilt off its block's up (deg) and its turn rate about the axle relative to the ship (rad/s) - the harness.</summary>
    internal bool FootAttitude(out float tiltDeg, out float spin)
    {
        tiltDeg = 0f; spin = 0f;
        var wheel = _cm?.WheelEntity; var grid = Entity?.GetTopLevelParent();
        if (Variant?.Foot == null || wheel == null || grid == null || !GridPhysics.Alive(wheel.Data) || !GridPhysics.Alive(grid.Data)) return false;
        var ww = wheel.Data.GetWorldTransform(); var bw = Entity.Data.GetWorldTransform();
        double d = Vector3D.Dot(Vector3D.Transform(Vector3D.Up, ww.Orientation), Vector3D.Transform(Vector3D.Up, bw.Orientation));
        tiltDeg = (float)(Math.Acos(Math.Clamp(d, -1.0, 1.0)) * 57.2958);
        if (GridPhysics.TryGetVelocity(wheel.Data, out _, out Vector3 wA) && GridPhysics.TryGetVelocity(grid.Data, out _, out Vector3 gA))
            spin = Vector3.Dot(wA - gA, Vector3.Transform(Vector3.Right, ww.Orientation));
        return true;
    }
}
