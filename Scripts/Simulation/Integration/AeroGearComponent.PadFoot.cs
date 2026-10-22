#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels;
using Keen.VRage.Core;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Colliders;
using Keen.VRage.Physics.Constraints;
using Keen.VRage.Physics.Constraints.Arguments;
using Keen.VRage.Physics.Constraints.Data;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// THE PAD-FOOT GEARS (server; 2026-10-20, the user: "No lock, custom wheel model that is the landing gear, and constrained"; rebuilt
/// 2026-10-21 - the user's option 2, ONE slider). The vanilla suspension's corner module still spawns the foot - a vanilla landing-gear
/// mesh on a heavy wheel body (gen_padgear_defs.py) - with the block, every load and every new wheel; per wheel this gear then gives it:
///  - its COLLIDER: the corner module's sphere swapped for a box of the foot's bounds (IPhysics.SetBodyCollider; its mass kept, its
///    inertia the box's) - the foot stands flat and slides (it has no axle to roll on any more);
///  - ITS ONE CONSTRAINT, THE SLIDER (Gear/PadSlider: a 6-DOF prismatic - the strut axis limited, everything else fixed - the foot held
///    level and still against the ship whatever it lands on);
///  - THE VANILLA WHEEL CONSTRAINT RELEASED (between the server's ticks, once the slider holds: its ConstraintData taken off the block -
///    the vanilla jobs that write the constraint's steering / brakes / motor / spring need it - then the constraint destroyed and the
///    corner module's _constraint cleared, so its own removal does not destroy it twice; REFLECTION_AND_WHITELIST §3.9). Nothing else
///    in the game reads it (the corner module's suspension distance is its geometry, the contacts are the wheel body's).
/// The OLEO (AeroWheelShockComponent's pad branch: WheelOleo rated by the foot - PadFootSpec.Rating) carries the load along the slider;
/// the slider's limits are its hard end stops. Retraction moves the limits (AeroGearComponent.cs: the rate-limited travel, collision
/// off deep in the well).
/// </summary>
public partial class AeroGearComponent
{
    /// <summary>Harness: the foot's box collider (true) or the corner module's sphere (false) - new wheels only.</summary>
    public static bool PadBoxCollider = true;
    /// <summary>The engine's LinearX sense against the strut's compression (+1: x = (foot pivot - mount) . axis; -1 the other way). Measured
    /// in game 2026-10-21 (padgear_test.sh A: the unloaded feet hang at -Extra, retracting moves them up). New sliders only.</summary>
    public static int PadSliderSense = 1;
    /// <summary>Harness: the slider in the engine's constraint group (false: the direct solver with the ship's other grouped constraints -
    /// stiffer) or excluded from it (true: the iterative solver, as the vanilla wheel constraint). New sliders only.</summary>
    public static bool PadSliderExcluded = false;
    /// <summary>The slider's kind (new sliders only): 0 the engine's own prismatic joint (PrismaticConstraintArgs - default since
    /// 2026-10-21: the 6-DOF's fixed angular rows let a foot tilt 26 deg on a 1.8 m/s touchdown in game), 1 the 6-DOF as a prismatic.</summary>
    public static int PadSliderKind = 0;
    /// <summary>Harness: false keeps the vanilla wheel constraint (the slider alone made - double-constrained: a comparison only).</summary>
    public static bool PadReleaseVanilla = true;
    public static int PadSliders, PadSlidersLost, PadBoxes, PadReleased;
    public static string PadFault = "";

    readonly PadSliderLife _sliderLife = new();
    IPhysicsConstraint _slider; ISixDegreesOfFreedomConstraint _slider6; IPrismaticConstraint _sliderP; Func<bool> _sliderDead; Entity _sliderGrid;
    float _sliderLo = float.NaN, _sliderHi = float.NaN;
    Entity _boxWheel; IPhysicsCollider _box, _oldBox; Entity _oldBoxWheel;
    Entity _releasedWheel; volatile bool _releaseQueued; volatile bool _inScene;
    // the suspension's frame (block space) - read once per wheel
    WheelCornerModuleComponent.SuspensionInfo _padSusp; bool _padFrames;
    string _padStatus = "";

    /// <summary>The foot rides the slider (the gear's step: the travel's limits, ShockUsable).</summary>
    internal bool PadHeld => _sliderLife.Has && (_slider6 != null || _sliderP != null);
    /// <summary>The foot's strut (its stroke and extra): the shocks' pad branch rates the foot with it.</summary>
    internal PadFootSpec Foot => Variant?.Foot;

    /// <summary>The gear's step for a pad foot: the box, the slider, the vanilla constraint released, the limits for the travel.</summary>
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
            string fr = "";
            var mid = physics.GetBodyMaterial(wheel.DEntity);
            if (mid.IsValid) { var mc = physics.Materials.Get(mid); fr = $", friction {mc.StaticFriction:F2} / {mc.DynamicFriction:F2} ({mc.FrictionCombinePolicy})"; }
            Log.Default?.Info($"[AERO-GEAR] {_key} pad foot: box collider {foot.BoxHalf.X * 2:F2} x {foot.BoxHalf.Y * 2:F2} x {foot.BoxHalf.Z * 2:F2} m, {mass:F0} kg{fr}");
        }

        // the slider
        var did = _sliderLife.Step(alive ? wheel : null, _sliderDead != null && _sliderDead());
        if (!_sliderLife.Has && _slider != null) { _slider = null; _slider6 = null; _sliderP = null; _sliderDead = null; _sliderGrid = null; PadSlidersLost++; }   // (gone with its body)
        if (did == PadSliderDo.Create) _sliderLife.Made(wheel, MakeSlider(physics, wheel, foot));

        // the vanilla wheel constraint released once the slider holds this wheel
        if (PadHeld && PadReleaseVanilla && !ReferenceEquals(_releasedWheel, wheel) && !_releaseQueued) QueueRelease(wheel);

        // the limits: the travel's (written only when they change)
        if (PadHeld)
        {
            PadSlider.Limits(_travel, foot.Stroke, PadExtra(foot), out float min, out float max);
            PadSlider.ToAxis(min, max, PadSliderSense, out float lo, out float hi);
            if (lo != _sliderLo || hi != _sliderHi)
            {
                if (_sliderP != null) { _sliderP.MinLinearLimit = lo; _sliderP.MaxLinearLimit = hi; }
                else _slider6.SetAxisLimits(ConstraintAxisIdentifier.LinearX, lo, hi);
                _sliderLo = lo; _sliderHi = hi;
            }
        }
    }

    /// <summary>The foot's extra stroke below the rest (m): Wheels.OleoExtension x its stroke (the slider's extended end).</summary>
    static float PadExtra(PadFootSpec foot) => foot.Stroke * Math.Clamp(WheelShockSettings.Extension, 0f, 1f);

    /// <summary>The suspension's frame (once per wheel): the corner module's _suspension (§3.9). (The foot's pivot dummy is the spec's:
    /// PadFootSpec.PivotPosition / PivotOrientation - the model component is in an assembly a mod cannot reference.)</summary>
    bool PadFrames()
    {
        if (_padFrames) return true;
        if (!_fieldLooked) LookFields();
        if (_suspensionField == null || _suspensionField.FieldType != typeof(WheelCornerModuleComponent.SuspensionInfo)) { _padStatus = "no _suspension"; return false; }
        _padSusp = (WheelCornerModuleComponent.SuspensionInfo)_suspensionField.GetValue(_cm);
        _padFrames = true;
        return true;
    }

    /// <summary>The slider: frames from the geometry (the foot's pivot; the block in the grid x the suspension's idle frame), never from
    /// the poses now - a foot that spawned off its line is pulled onto it. On a load with the leg up the travel starts where the foot is.</summary>
    bool MakeSlider(IPhysics physics, Entity wheel, PadFootSpec foot)
    {
        var grid = Entity.GetTopLevelParent();
        if (grid == null || !GridPhysics.Alive(grid.Data) || !physics.HasBody(wheel.DEntity) || !physics.HasBody(grid.DEntity)) return false;
        if (!PadFrames() || !Entity.Data.TryGet<RelativeTransform>(out var blockRel)) return false;
        var mount = blockRel * _padSusp.IdleTransform;   // (as the corner module builds its constraint's B)
        // the travel now: down - the stroke; a gear loaded up (the saved sequence) - held where the foot is
        float x = _cm.CalculateSuspensionDistance(wheel);
        if (Seq.Progress < 1f && float.IsFinite(x)) _travel = Math.Clamp(x, 0f, Variant?.RetractTravel ?? 2.5f);
        PadSlider.Limits(_travel, foot.Stroke, PadExtra(foot), out float min, out float max);
        PadSlider.ToAxis(min, max, PadSliderSense, out float lo, out float hi);
        Matrix pa, pb; int kind = PadSliderKind;
        if (kind == 1)
        {
            var args = new SixDegreesOfFreedomConstraintArgs
            {
                PivotA = pa = PadSlider.Frame(foot.PivotPosition, foot.PivotOrientation),
                PivotB = pb = PadSlider.Frame(mount.Position, mount.Orientation),
                ExcludeFromGroup = PadSliderExcluded,
            };
            for (int i = 0; i < 6; i++) args.SetAxisMode((ConstraintAxisIdentifier)i, (ConstraintAxisMode)PadSlider.Modes[i]);
            args.SetAxisLimits(ConstraintAxisIdentifier.LinearX, lo, hi);
            _slider = physics.AllocateConstraint(wheel.DEntity, grid.DEntity, args);   // (A the foot, constrained to B the ship)
            if (_slider == null || !_slider.TryGet<ISixDegreesOfFreedomConstraint>(out _slider6)) { _padStatus = "slider refused"; _slider = null; return false; }
        }
        else
        {
            pa = PadSlider.PrismaticFrame(foot.PivotPosition, foot.PivotOrientation);
            pb = PadSlider.PrismaticFrame(mount.Position, mount.Orientation);
            var args = new PrismaticConstraintArgs(in pa, in pb) { Limits = new Vector2(lo, hi), ExcludeFromGroup = PadSliderExcluded };
            _slider = physics.AllocateConstraint(wheel.DEntity, grid.DEntity, args);
            if (_slider == null || !_slider.TryGet<IPrismaticConstraint>(out _sliderP)) { _padStatus = "slider refused"; _slider = null; return false; }
        }
        _sliderLo = lo; _sliderHi = hi;
        _sliderDead = StructureTear.DestroyedOf(_slider); _sliderGrid = grid; PadSliders++;
        _padStatus = "slider";
        Log.Default?.Info($"[AERO-GEAR] {_key} pad foot: slider on ({(kind == 1 ? "6-DOF" : "prismatic")}, x {x:F3} m, limits {min:F3}..{max:F3} m, axis {lo:F3}..{hi:F3}, sense {PadSliderSense}, {(PadSliderExcluded ? "excluded from" : "in")} the group); "
            + $"A pos ({foot.PivotPosition.X:F3}, {foot.PivotPosition.Y:F3}, {foot.PivotPosition.Z:F3}) rows {Row(pa.Row1)} {Row(pa.Row2)} {Row(pa.Row3)}; "
            + $"B pos ({mount.Position.X:F3}, {mount.Position.Y:F3}, {mount.Position.Z:F3}) rows {Row(pb.Row1)} {Row(pb.Row2)} {Row(pb.Row3)}");
        return true;
    }
    static string Row(Vector4 r) => $"({r.X:F3}, {r.Y:F3}, {r.Z:F3})";

    // ── the vanilla wheel constraint, released between the server's ticks ──
    static MethodInfo _removeConstraintData; static bool _releaseLooked;

    void QueueRelease(Entity wheel)
    {
        var q = Keen.VRage.Core.VRageCore.Instance?.UpdateQueue;
        if (q == null) { _padStatus = "no main loop queue"; return; }
        _releaseQueued = true;
        q.Enqueue(() =>
        {
            try { ReleaseVanilla(wheel); }
            catch (Exception e) { PadFault = "release: " + (e.InnerException ?? e).Message; _releasedWheel = wheel; Log.Default?.Info("[AERO-GEAR] " + _key + " pad foot: the vanilla wheel constraint not released: " + e); }
            finally { _releaseQueued = false; }
        });
    }

    /// <summary>On the main loop, the server between ticks: the block's ConstraintData off (the vanilla jobs that write the wheel
    /// constraint stop), the constraint destroyed, the corner module's field cleared - only while this block, this wheel and the slider
    /// still are.</summary>
    void ReleaseVanilla(Entity wheel)
    {
        if (!_inScene || _cm == null || !ReferenceEquals(_cm.WheelEntity, wheel) || !GridPhysics.Alive(wheel.Data) || !PadHeld) return;
        if (!_fieldLooked) LookFields();
        if (!_releaseLooked)
        {
            _releaseLooked = true;
            var cd = typeof(WheelCornerModuleComponent).GetNestedType("ConstraintData", BindingFlags.NonPublic);
            var m = typeof(DEntityContext).GetMethod("TryRemove", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            _removeConstraintData = cd != null && m != null && m.IsGenericMethodDefinition ? m.MakeGenericMethod(cd) : null;
            if (_removeConstraintData == null) Log.Default?.Info("[AERO-GEAR] WheelCornerModuleComponent.ConstraintData / DEntityContext.TryRemove<T> not found: pad feet keep the wheel constraint");
        }
        if (_constraintField == null || _removeConstraintData == null) { _releasedWheel = wheel; PadFault = "the wheel constraint cannot be released (reflection)"; return; }
        var c = _constraintField.GetValue(_cm) as IPhysicsConstraint;
        _releasedWheel = wheel;
        if (c == null) return;
        _removeConstraintData.Invoke(Entity.Data, null);   // (first: nothing writes the constraint after this - the corner module is on this block)
        GridPhysics.Physics?.DestroyConstraint(c);
        _constraintField.SetValue(_cm, null);           // (the corner module's removal: nothing to destroy again)
        _wc = null;
        PadReleased++;
        Log.Default?.Info($"[AERO-GEAR] {_key} pad foot: the vanilla wheel constraint released - the slider alone holds the foot");
    }

    /// <summary>The block going: the slider destroyed while both bodies live (else it went with them).</summary>
    void PadFootRemoved()
    {
        _inScene = false;
        bool dead = _sliderDead != null && _sliderDead();
        var wheel = _cm?.WheelEntity;
        bool bodies = _slider != null && wheel != null && GridPhysics.Alive(wheel.Data) && _sliderGrid != null && GridPhysics.Alive(_sliderGrid.Data);
        if (_sliderLife.Removing(bodies, dead) == PadSliderDo.Destroy) GridPhysics.Physics?.DestroyConstraint(_slider);
        _slider = null; _slider6 = null; _sliderP = null; _sliderDead = null; _sliderGrid = null; _boxWheel = null; _sliderLo = _sliderHi = float.NaN;
    }

    /// <summary>The foot's turn rate relative to the ship in the foot's own frame (rad/s: X its Right, Y its Up, Z its Backward) - the
    /// harness's readout (WheelShockDev).</summary>
    internal bool FootRates(out Vector3 relLocal)
    {
        relLocal = Vector3.Zero;
        var wheel = _cm?.WheelEntity; var grid = Entity?.GetTopLevelParent();
        if (Variant?.Foot == null || wheel == null || grid == null || !GridPhysics.Alive(wheel.Data) || !GridPhysics.Alive(grid.Data)) return false;
        if (!GridPhysics.TryGetVelocity(wheel.Data, out _, out Vector3 wA) || !GridPhysics.TryGetVelocity(grid.Data, out _, out Vector3 gA)) return false;
        relLocal = Vector3.Transform(wA - gA, Quaternion.Inverse(wheel.Data.GetWorldTransform().Orientation));
        return true;
    }

    /// <summary>The foot's tilt off its block's up (deg) and its turn rate against the ship (rad/s, the whole relative rate) - the harness.</summary>
    internal bool FootAttitude(out float tiltDeg, out float spin)
    {
        tiltDeg = 0f; spin = 0f;
        var wheel = _cm?.WheelEntity; var grid = Entity?.GetTopLevelParent();
        if (Variant?.Foot == null || wheel == null || grid == null || !GridPhysics.Alive(wheel.Data) || !GridPhysics.Alive(grid.Data)) return false;
        var ww = wheel.Data.GetWorldTransform(); var bw = Entity.Data.GetWorldTransform();
        double d = Vector3D.Dot(Vector3D.Transform(Vector3D.Up, ww.Orientation), Vector3D.Transform(Vector3D.Up, bw.Orientation));
        tiltDeg = (float)(Math.Acos(Math.Clamp(d, -1.0, 1.0)) * 57.2958);
        if (GridPhysics.TryGetVelocity(wheel.Data, out _, out Vector3 wA) && GridPhysics.TryGetVelocity(grid.Data, out _, out Vector3 gA))
            spin = (wA - gA).Length();
        return true;
    }
}
